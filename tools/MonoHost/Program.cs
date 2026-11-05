using System;
using System.IO;
using System.Runtime.InteropServices;

// Usage: MonoHost <EmbedRuntime dir> <etc dir> <assemblies dir> <exe> [args...]
// Starts the given mono runtime the way the player does and runs a managed exe in it.
internal static class Program
{
    private const string Mono = "mono-2.0-bdwgc.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);
    [DllImport(Mono)] private static extern void mono_set_assemblies_path([MarshalAs(UnmanagedType.LPStr)] string path);
    [DllImport(Mono)] private static extern void mono_set_dirs([MarshalAs(UnmanagedType.LPStr)] string assemblies, [MarshalAs(UnmanagedType.LPStr)] string config);
    [DllImport(Mono)] private static extern IntPtr mono_jit_init_version([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string version);
    [DllImport(Mono)] private static extern IntPtr mono_domain_assembly_open(IntPtr domain, [MarshalAs(UnmanagedType.LPStr)] string path);
    [DllImport(Mono)] private static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int argc, IntPtr[] argv);

    private static int Main(string[] args)
    {
        string runtime = args[0];
        string etc = args[1];
        string assemblies = args[2];
        string exe = args[3];

        SetDllDirectory(runtime);
        if (LoadLibrary(Path.Combine(runtime, Mono)) == IntPtr.Zero)
        {
            Console.WriteLine("LoadLibrary failed: " + Marshal.GetLastWin32Error());
            return 2;
        }
        mono_set_dirs(assemblies, etc);
        mono_set_assemblies_path(assemblies);
        IntPtr domain = mono_jit_init_version("harness", "v4.0.30319");
        IntPtr assembly = mono_domain_assembly_open(domain, exe);
        if (assembly == IntPtr.Zero)
        {
            Console.WriteLine("Could not open " + exe);
            return 3;
        }
        IntPtr[] argv = new IntPtr[args.Length - 3];
        for (int i = 0; i < argv.Length; i++)
        {
            argv[i] = Marshal.StringToHGlobalAnsi(args[i + 3]);
        }
        return mono_jit_exec(domain, assembly, argv.Length, argv);
    }
}
