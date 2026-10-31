using System;
using System.Collections.Generic;
using System.IO;

namespace Qingye.Core
{
    // Stores offsets only, never file contents. All entries die with the process.
    internal sealed class Gb18030PageIndex
    {
        const int MaximumCachedFiles = 4;
        const int MaximumCheckpoints = 1024 * 1024;
        static readonly object cacheLock = new object();
        static readonly Dictionary<string, Lazy<Gb18030PageIndex>> cache = new Dictionary<string, Lazy<Gb18030PageIndex>>();
        static readonly List<string> insertionOrder = new List<string>();
        readonly long interval;
        readonly long[] offsets;
        internal static int CachedEntryCount { get { lock (cacheLock) return insertionOrder.Count; } }

        Gb18030PageIndex(string path, long length, int pageSize)
        {
            long pageCount = (length + pageSize - 1) / pageSize;
            long stride = Math.Max(1, (pageCount + MaximumCheckpoints - 1) / MaximumCheckpoints);
            interval = stride * pageSize;
            offsets = new long[(int)Math.Max(1, (length + interval - 1) / interval)];
            Build(path);
        }

        public static long Before(string path, long position, int pageSize)
        {
            var info = new FileInfo(path);
            string key = path.ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.CreationTimeUtc.Ticks + "|" + pageSize;
            Lazy<Gb18030PageIndex> pending;
            lock (cacheLock)
            {
                if (!cache.TryGetValue(key, out pending))
                {
                    long length = info.Length;
                    pending = new Lazy<Gb18030PageIndex>(() => new Gb18030PageIndex(path, length, pageSize));
                    while (cache.Count >= MaximumCachedFiles)
                    {
                        cache.Remove(insertionOrder[0]);
                        insertionOrder.RemoveAt(0);
                    }
                    cache.Add(key, pending);
                    insertionOrder.Add(key);
                }
            }
            try
            {
                var index = pending.Value;
                return index.offsets[(int)Math.Min(index.offsets.Length - 1, position / index.interval)];
            }
            catch
            {
                // A failed build must not permanently poison a subsequent read.
                lock (cacheLock)
                {
                    Lazy<Gb18030PageIndex> current;
                    if (cache.TryGetValue(key, out current) && Object.ReferenceEquals(current, pending))
                    {
                        cache.Remove(key);
                        insertionOrder.Remove(key);
                    }
                }
                throw;
            }
        }

        void Build(string path)
        {
            byte[] buffer = new byte[65536];
            int pendingBytes = 0;
            int checkpoint = 0;
            long position = 0;
            long characterStart = 0;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    for (int offset = 0; offset < count; offset++, position++)
                    {
                        if (checkpoint < offsets.Length && position == checkpoint * interval)
                            offsets[checkpoint++] = pendingBytes == 0 ? position : characterStart;
                        byte value = buffer[offset];
                        if (pendingBytes == 0)
                        {
                            characterStart = position;
                            pendingBytes = value >= 0x81 && value <= 0xfe ? 1 : 0;
                        }
                        else if (pendingBytes == 1)
                            pendingBytes = value >= 0x30 && value <= 0x39 ? 2 : 0;
                        else if (pendingBytes == 2)
                            pendingBytes = value >= 0x81 && value <= 0xfe ? 3 : 0;
                        else
                            pendingBytes = 0;
                    }
                }
            }
        }
    }
}
