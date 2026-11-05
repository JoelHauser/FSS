using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FirstShotSmoothness.Diagnostics
{
    /// <summary>
    /// Records every JIT compile and GC phase the Mono runtime performs, with timestamps, using
    /// the runtime's own profiler callbacks. A release player offers no other way to see these:
    /// the Unity profiler is absent and timing alone cannot tell JIT from anything else.
    ///
    /// The callbacks run inside the JIT and the GC, where managed code must not run: it could
    /// need JIT itself, or take a lock the runtime already holds. So the callback is a few
    /// instructions of machine code that claim a slot in an unmanaged ring and store the event.
    /// No lock, no allocation, safe from any thread. Everything else happens later, off the
    /// measured path. x64 Windows only, which is all the game ships for.
    /// </summary>
    internal static unsafe class NativeEvents
    {
        public const long KindJitBegin = 1;
        public const long KindJitDone = 2;
        public const long KindGc = 3;
        public const long KindMethodFree = 4;

        private const string Mono = "mono-2.0-bdwgc.dll";
        private const int Capacity = 1 << 17;
        private const int HeaderBytes = 16;
        private const int EntryBytes = 32;
        private const int KindImmediateOffset = 45;

        [DllImport(Mono)] private static extern IntPtr mono_profiler_create(IntPtr prof);
        [DllImport(Mono)] private static extern void mono_profiler_set_jit_begin_callback(IntPtr handle, IntPtr cb);
        [DllImport(Mono)] private static extern void mono_profiler_set_jit_done_callback(IntPtr handle, IntPtr cb);
        [DllImport(Mono)] private static extern void mono_profiler_set_gc_event_callback(IntPtr handle, IntPtr cb);
        [DllImport(Mono)] private static extern void mono_profiler_set_method_free_callback(IntPtr handle, IntPtr cb);
        [DllImport(Mono)] private static extern IntPtr mono_method_full_name(IntPtr method, int signature);
        [DllImport(Mono)] private static extern void mono_free(IntPtr ptr);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long ReadTscDelegate();

        private static long* _ring;
        private static ReadTscDelegate _readTsc;

        public static bool Installed { get; private set; }

        public static string FailureReason { get; private set; }

        /// <summary>The Windows id of the thread that installed this: the game's main thread.</summary>
        public static uint MainThreadId { get; private set; }

        /// <summary>How many events have been claimed since install, overwritten ones included.</summary>
        public static long Count => _ring == null ? 0 : Volatile.Read(ref _ring[0]);

        public static int RingCapacity => Capacity;

        public static bool Install()
        {
            if (Installed)
            {
                return true;
            }
            try
            {
                if (IntPtr.Size != 8)
                {
                    throw new PlatformNotSupportedException("not a 64-bit process");
                }

                // The ring lives outside the managed heap for the life of the process: the
                // callbacks cannot be removed once set, so it must never go away.
                int longs = (HeaderBytes + Capacity * EntryBytes) / 8;
                IntPtr data = Marshal.AllocHGlobal(longs * 8);
                long* ring = (long*)data;
                for (int i = 0; i < longs; i++)
                {
                    ring[i] = 0;
                }
                ring[1] = Capacity - 1;
                _ring = ring;

                IntPtr code = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
                if (code == IntPtr.Zero)
                {
                    throw new InvalidOperationException("VirtualAlloc failed, error " + Marshal.GetLastWin32Error());
                }
                int at = 0;
                IntPtr jitBegin = Place(code, ref at, Thunk(KindJitBegin));
                IntPtr jitDone = Place(code, ref at, Thunk(KindJitDone));
                IntPtr gc = Place(code, ref at, Thunk(KindGc));
                IntPtr methodFree = Place(code, ref at, Thunk(KindMethodFree));
                // rdtsc; shl rdx, 32; or rax, rdx; ret
                IntPtr tsc = Place(code, ref at, new byte[] { 0x0F, 0x31, 0x48, 0xC1, 0xE2, 0x20, 0x48, 0x09, 0xD0, 0xC3 });
                if (!VirtualProtect(code, (UIntPtr)4096, 0x20, out _))
                {
                    throw new InvalidOperationException("VirtualProtect failed, error " + Marshal.GetLastWin32Error());
                }

                _readTsc = (ReadTscDelegate)Marshal.GetDelegateForFunctionPointer(tsc, typeof(ReadTscDelegate));
                _readTsc();
                MainThreadId = GetCurrentThreadId();

                IntPtr handle = mono_profiler_create(data);
                mono_profiler_set_jit_begin_callback(handle, jitBegin);
                mono_profiler_set_jit_done_callback(handle, jitDone);
                mono_profiler_set_gc_event_callback(handle, gc);
                mono_profiler_set_method_free_callback(handle, methodFree);

                Installed = true;
                return true;
            }
            catch (Exception e)
            {
                FailureReason = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// Machine code for one callback. Win64 convention: rcx = the pointer given to
        /// mono_profiler_create (the ring), rdx = the MonoMethod* or GC event. Uses only
        /// volatile registers.
        /// </summary>
        private static byte[] Thunk(long kind)
        {
            byte[] code =
            {
                0x49, 0x89, 0xD1,                            // mov  r9, rdx          ; keep the argument
                0x49, 0x89, 0xCA,                            // mov  r10, rcx         ; ring
                0xB8, 0x01, 0x00, 0x00, 0x00,                // mov  eax, 1
                0xF0, 0x49, 0x0F, 0xC1, 0x02,                // lock xadd [r10], rax  ; claim a slot
                0x49, 0x23, 0x42, 0x08,                      // and  rax, [r10+8]     ; wrap
                0x48, 0xC1, 0xE0, 0x05,                      // shl  rax, 5           ; * EntryBytes
                0x4D, 0x8D, 0x54, 0x02, 0x10,                // lea  r10, [r10+rax+16]
                0x0F, 0x31,                                  // rdtsc
                0x48, 0xC1, 0xE2, 0x20,                      // shl  rdx, 32
                0x48, 0x09, 0xD0,                            // or   rax, rdx
                0x49, 0x89, 0x02,                            // mov  [r10], rax       ; time
                0x49, 0xC7, 0x42, 0x08, 0, 0, 0, 0,          // mov  qword [r10+8], kind
                0x4D, 0x89, 0x4A, 0x10,                      // mov  [r10+16], r9     ; argument
                0x65, 0x48, 0x8B, 0x04, 0x25, 0x48, 0, 0, 0, // mov  rax, gs:[0x48]   ; TEB thread id
                0x49, 0x89, 0x42, 0x18,                      // mov  [r10+24], rax
                0xC3                                         // ret
            };
            code[KindImmediateOffset] = (byte)kind;
            return code;
        }

        private static IntPtr Place(IntPtr code, ref int at, byte[] bytes)
        {
            IntPtr where = code + at;
            Marshal.Copy(bytes, 0, where, bytes.Length);
            at += (bytes.Length + 15) & ~15;
            return where;
        }

        /// <summary>A matching pair of the two clocks, to convert event times to the Stopwatch's.</summary>
        public static void SampleClocks(out long tsc, out long qpc)
        {
            long before = Stopwatch.GetTimestamp();
            tsc = _readTsc();
            qpc = before + (Stopwatch.GetTimestamp() - before) / 2;
        }

        /// <summary>Copies one event out of the ring. Index counts up from 0, as <see cref="Count"/> does.</summary>
        public static void Read(long index, out long tsc, out long kind, out long arg, out long thread)
        {
            long* entry = (long*)((byte*)_ring + HeaderBytes + (index & (Capacity - 1)) * EntryBytes);
            tsc = entry[0];
            kind = entry[1];
            arg = entry[2];
            thread = entry[3];
        }

        /// <summary>
        /// The method's full name. Touches runtime structures, so call it after the measured
        /// interval. Following a freed method's pointer crashes the game, and no exception can
        /// catch that: only call this for a pointer <see cref="FreedMethods"/> has just cleared.
        /// </summary>
        public const string UnreadableName = "<unreadable name>";

        public static string MethodName(IntPtr method)
        {
            IntPtr text = mono_method_full_name(method, 1);
            if (text == IntPtr.Zero)
            {
                return "?";
            }
            try
            {
                return ReadName(text);
            }
            finally
            {
                mono_free(text);
            }
        }

        /// <summary>
        /// Reads the bytes itself: Marshal.PtrToStringAnsi throws on invalid UTF-8, and one
        /// unreadable name then cost a report every name after it.
        /// </summary>
        public static string ReadName(IntPtr text)
        {
            int length = 0;
            while (Marshal.ReadByte(text, length) != 0)
            {
                length++;
            }
            var bytes = new byte[length];
            Marshal.Copy(text, bytes, 0, length);
            string name = Encoding.UTF8.GetString(bytes);
            foreach (char c in name)
            {
                if (c == '�' || char.IsControl(c))
                {
                    return UnreadableName;
                }
            }
            return name;
        }
    }

    /// <summary>
    /// Which methods the runtime has freed, from the ring's free events. Dynamic methods are freed
    /// once collected, and that goes on after a raid, so refresh right before each name.
    /// </summary>
    internal sealed class FreedMethods
    {
        private readonly HashSet<long> _freed = new HashSet<long>();
        private long _next;

        public FreedMethods(long first)
        {
            _next = first;
        }

        public int Count => _freed.Count;

        /// <summary>False if unread events were overwritten, so a free may have been missed.</summary>
        public bool Refresh()
        {
            long end = NativeEvents.Count;
            if (end - _next >= NativeEvents.RingCapacity)
            {
                return false;
            }
            for (; _next < end; _next++)
            {
                NativeEvents.Read(_next, out _, out long kind, out long arg, out _);
                if (kind == NativeEvents.KindMethodFree)
                {
                    _freed.Add(arg);
                }
                else if (kind == NativeEvents.KindJitBegin || kind == NativeEvents.KindJitDone)
                {
                    // The allocator reused the address for a new, live method.
                    _freed.Remove(arg);
                }
            }
            return true;
        }

        public bool Contains(long method)
        {
            return _freed.Contains(method);
        }
    }
}
