using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace FirstShotSmoothness.Diagnostics
{
    /// <summary>
    /// Turns a finished session into files. Runs on a background thread and touches no Unity API:
    /// it reads only the session's own arrays and the runtime event ring, both of which nothing
    /// else writes to any more apart from the ring's newer entries, which it ignores.
    /// </summary>
    internal static class ReportWriter
    {
        public struct NativeRange
        {
            public long First;
            public long Last;
            public long TscStart;
            public long QpcStart;
            public long TscEnd;
            public long QpcEnd;
        }

        public static void Write(string folder, Session session, string header, IReadOnlyList<string> names, NativeRange native)
        {
            Directory.CreateDirectory(folder);
            long nativeDropped = 0;
            if (native.Last - native.First > NativeEvents.RingCapacity)
            {
                nativeDropped = native.Last - native.First - NativeEvents.RingCapacity;
                native.First = native.Last - NativeEvents.RingCapacity;
            }

            using (var text = new StreamWriter(Path.Combine(folder, "session.txt")))
            {
                text.Write(header);
                text.WriteLine();
                text.WriteLine("[counts]");
                text.WriteLine("stopwatch_frequency=" + I(Stopwatch.Frequency));
                text.WriteLine("frames_seen=" + I(session.FramesSeen));
                text.WriteLine("frames_kept=" + I(session.KeptCount));
                text.WriteLine("frames_dropped=" + I(session.FramesDropped));
                text.WriteLine("events=" + I(session.EventCount));
                text.WriteLine("events_dropped=" + I(session.EventsDropped));
                text.WriteLine("shots=" + I(session.ShotCount));
                text.WriteLine("shots_dropped=" + I(session.ShotsDropped));
                text.WriteLine("native_events=" + I(native.Last - native.First));
                text.WriteLine("native_events_dropped=" + I(nativeDropped));
                text.WriteLine("frames_before=" + I(session.FramesBefore));
                text.WriteLine("frames_after=" + I(session.FramesAfter));
            }

            WriteFrames(folder, session);
            WriteEvents(folder, session);
            WriteShots(folder, session);
            WriteHistogram(folder, session);
            WriteNames(folder, names);

            var methods = new HashSet<long>();
            if (NativeEvents.Installed)
            {
                WriteNative(folder, native, methods);
            }

            // Last, because resolving a name is the one step that follows a raw pointer.
            using (var jit = new StreamWriter(Path.Combine(folder, "jit_names.csv")))
            {
                jit.WriteLine("method_ptr,name");
                foreach (long method in methods)
                {
                    jit.WriteLine(I(method) + ",\"" + NativeEvents.MethodName((IntPtr)method).Replace("\"", "'") + "\"");
                }
            }
        }

        private static void WriteFrames(string folder, Session s)
        {
            using (var file = new StreamWriter(Path.Combine(folder, "frames.csv")))
            {
                file.Write("frame");
                for (int m = 0; m < (int)Mark.Count; m++)
                {
                    file.Write(',');
                    file.Write(((Mark)m).ToString());
                }
                file.WriteLine(",gc_count,heap_bytes,gc_mode,gpu_us,cpu_us,physics_steps");
                for (int i = 0; i < s.KeptCount; i++)
                {
                    int at = i * Session.FrameStride;
                    file.Write(I(s.Kept[at]));
                    for (int f = 1; f < Session.FrameStride; f++)
                    {
                        file.Write(',');
                        file.Write(I(s.Kept[at + f]));
                    }
                    file.WriteLine();
                }
            }
        }

        private static void WriteEvents(string folder, Session s)
        {
            using (var file = new StreamWriter(Path.Combine(folder, "events.csv")))
            {
                file.WriteLine("qpc,frame,kind,probe,shot,inclusive_ticks,exclusive_ticks,a,b");
                for (int i = 0; i < s.EventCount; i++)
                {
                    int at = i * Session.EventStride;
                    file.Write(I(s.Events[at + Session.EventTime]));
                    file.Write(',');
                    file.Write(I(s.Events[at + Session.EventFrame]));
                    file.Write(',');
                    file.Write(((EventKind)s.Events[at + Session.EventKindField]).ToString());
                    file.Write(',');
                    file.Write(EventProbeName(s.Events[at + Session.EventKindField], s.Events[at + Session.EventProbe]));
                    for (int f = Session.EventShot; f < Session.EventStride; f++)
                    {
                        file.Write(',');
                        file.Write(I(s.Events[at + f]));
                    }
                    file.WriteLine();
                }
            }
        }

        private static string EventProbeName(long kind, long probe)
        {
            return kind == (long)EventKind.Probe ? ((ProbeId)probe).ToString() : "";
        }

        private static void WriteShots(string folder, Session s)
        {
            using (var file = new StreamWriter(Path.Combine(folder, "shots.csv")))
            {
                file.WriteLine("shot,frame,qpc,shooter,weapon_template,weapon_instance,ammo,flags,since_last_local_ticks,alive_players");
                for (int i = 0; i < s.ShotCount; i++)
                {
                    int at = i * Session.ShotStride;
                    for (int f = 0; f < Session.ShotStride; f++)
                    {
                        if (f > 0)
                        {
                            file.Write(',');
                        }
                        if (f == Session.ShotShooter)
                        {
                            file.Write(((Shooter)s.Shots[at + f]).ToString());
                        }
                        else if (f == Session.ShotFlagsField)
                        {
                            file.Write(((ShotFlags)s.Shots[at + f]).ToString().Replace(", ", "|"));
                        }
                        else
                        {
                            file.Write(I(s.Shots[at + f]));
                        }
                    }
                    file.WriteLine();
                }
            }
        }

        private static void WriteHistogram(string folder, Session s)
        {
            using (var file = new StreamWriter(Path.Combine(folder, "histogram.csv")))
            {
                file.WriteLine("bucket_ms,frames_away_from_shots,frames_near_shots");
                for (int b = 0; b < Session.HistogramBuckets; b++)
                {
                    long away = s.Histogram[b];
                    long near = s.Histogram[Session.HistogramBuckets + b];
                    if (away != 0 || near != 0)
                    {
                        file.WriteLine((b * Session.HistogramBucketMs).ToString("0.00", CultureInfo.InvariantCulture) + "," + I(away) + "," + I(near));
                    }
                }
            }
        }

        private static void WriteNames(string folder, IReadOnlyList<string> names)
        {
            using (var file = new StreamWriter(Path.Combine(folder, "names.csv")))
            {
                file.WriteLine("id,name");
                for (int i = 0; i < names.Count; i++)
                {
                    file.WriteLine(I(i) + ",\"" + names[i].Replace("\"", "'") + "\"");
                }
            }
        }

        private static void WriteNative(string folder, NativeRange range, HashSet<long> methods)
        {
            double scale = range.TscEnd > range.TscStart
                ? (double)(range.QpcEnd - range.QpcStart) / (range.TscEnd - range.TscStart)
                : 0;
            using (var file = new StreamWriter(Path.Combine(folder, "native.csv")))
            {
                file.WriteLine("qpc,kind,arg,main_thread");
                for (long i = range.First; i < range.Last; i++)
                {
                    NativeEvents.Read(i, out long tsc, out long kind, out long arg, out long thread);
                    long qpc = range.QpcStart + (long)((tsc - range.TscStart) * scale);
                    if (kind == NativeEvents.KindGc)
                    {
                        arg &= 0xFFFFFFFF;
                    }
                    else
                    {
                        methods.Add(arg);
                    }
                    string name = kind == NativeEvents.KindJitBegin ? "JitBegin" : kind == NativeEvents.KindJitDone ? "JitDone" : "Gc";
                    file.WriteLine(I(qpc) + "," + name + "," + I(arg) + "," + (thread == NativeEvents.MainThreadId ? "1" : "0"));
                }
            }
        }

        private static string I(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
