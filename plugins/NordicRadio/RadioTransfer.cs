using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace ValheimModPack.NordicRadio
{
    // File access only: callers run this on workers and marshal results to the game thread.
    internal static class RadioTransfer
    {
        internal static byte[] ReadChunk(LibraryEntry entry, long offset)
        {
            var info = new FileInfo(entry.Path);
            if (!info.Exists || info.Length != entry.Track.Size || info.LastWriteTimeUtc.Ticks != entry.Modified
                || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Host file changed; refresh the playlist");
            int length = (int)Math.Min(RadioProtocol.ChunkSize, entry.Track.Size - offset);
            if (offset < 0 || offset % RadioProtocol.ChunkSize != 0 || length <= 0) throw new InvalidDataException("Invalid read offset");
            var data = new byte[length];
            using (var file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            {
                file.Position = offset;
                int read = 0;
                while (read < length)
                {
                    int part = file.Read(data, read, length - read);
                    if (part == 0) throw new EndOfStreamException();
                    read += part;
                }
            }
            info.Refresh();
            if (info.Length != entry.Track.Size || info.LastWriteTimeUtc.Ticks != entry.Modified)
                throw new IOException("Host file changed while reading");
            return data;
        }
    }

    // At most one bounded batch is written at a time. Cancellation never waits on disk on the game thread.
    internal sealed class RadioDownloadWriter : IDisposable
    {
        private readonly RadioLibrary library;
        private readonly TrackInfo track;
        private readonly ICollection<string> protectedIds;
        private FileStream file;
        private SHA256 hash;
        private string temporary;
        private long offset;
        private int busy;
        private readonly object cleanupGate = new object();
        private volatile bool cancelled;

        internal RadioDownloadWriter(RadioLibrary library, TrackInfo track, ICollection<string> protectedIds)
        { this.library = library; this.track = track; this.protectedIds = protectedIds; }

        internal void Write(long expectedOffset, byte[][] blocks, Action<long, bool, Exception> completed)
        {
            if (cancelled || Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("Writer is busy or cancelled");
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool finished = false; Exception error = null;
                try
                {
                    if (cancelled) return;
                    if (expectedOffset != offset) throw new InvalidDataException("Out-of-order file write");
                    if (file == null)
                    {
                        temporary = library.BeginDownload(track, protectedIds);
                        file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
                        hash = SHA256.Create();
                    }
                    foreach (byte[] block in blocks)
                    {
                        if (cancelled) return;
                        if (block == null || block.Length != Math.Min(RadioProtocol.ChunkSize, track.Size - offset))
                            throw new InvalidDataException("Unexpected write size");
                        file.Write(block, 0, block.Length);
                        hash.TransformBlock(block, 0, block.Length, block, 0);
                        offset += block.Length;
                    }
                    if (offset == track.Size)
                    {
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        if (RadioLibrary.Hex(hash.Hash) != track.Id) throw new InvalidDataException("MP3 checksum mismatch");
                        file.Flush(); file.Dispose(); file = null;
                        if (cancelled) return;
                        library.CommitDownload(track.Id, temporary); temporary = null; finished = true;
                    }
                }
                catch (Exception exception) { error = exception; cancelled = true; }
                finally
                {
                    if (finished || cancelled) Cleanup();
                    Interlocked.Exchange(ref busy, 0);
                    if (cancelled) Cleanup();
                    completed(offset, finished, error);
                }
            });
        }

        private void Cleanup()
        {
            // This lock is only acquired by workers, never the game thread.
            lock (cleanupGate)
            {
                if (file != null) { try { file.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { } file = null; }
                if (hash != null) { hash.Dispose(); hash = null; }
                if (temporary != null)
                {
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    temporary = null;
                }
            }
        }
        public void Dispose()
        {
            cancelled = true;
            if (Interlocked.CompareExchange(ref busy, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(delegate { try { Cleanup(); } finally { Interlocked.Exchange(ref busy, 0); } });
        }
    }
}
