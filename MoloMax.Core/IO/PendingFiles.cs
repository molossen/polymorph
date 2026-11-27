using System.Collections.Concurrent;

namespace MoloMax.Core.IO;

/// <summary>
/// Files written in the background (the textures a game model uses, when it is read for the first time): the model does not
/// wait for them, and whoever needs one of these files before it is on disk waits for that one only.
/// </summary>
public static class PendingFiles
{
    static readonly ConcurrentDictionary<string, Task> writes = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim gate = new(3);

    /// <summary>Files still being written.</summary>
    public static int Count => writes.Count;

    /// <summary>Writes <paramref name="path"/> with the bytes <paramref name="make"/> gives, in the background (once per path).</summary>
    public static void Queue(string path, Func<byte[]> make)
    {
        path = Path.GetFullPath(path);
        writes.GetOrAdd(path, p => Task.Run(() =>
        {
            gate.Wait();
            try
            {
                var bytes = make();
                if (bytes != null)
                {
                    // written whole under another name, then renamed: nobody reads half a file
                    var tmp = p + ".part";
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, p, true);
                }
            }
            catch { }
            finally { gate.Release(); writes.TryRemove(p, out _); }
        }));
    }

    public static bool IsPending(string path) => writes.ContainsKey(Path.GetFullPath(path));

    /// <summary>Waits for every file on its way (closing the app: a model kept in the cache must find its textures next time).</summary>
    public static void WaitAll(int milliseconds)
    {
        try { Task.WaitAll(writes.Values.ToArray(), milliseconds); } catch { }
    }

    /// <summary>Returns once the file is written (at once when nothing is writing it).</summary>
    public static void Wait(string path)
    {
        if (writes.TryGetValue(Path.GetFullPath(path), out var t)) { try { t.Wait(); } catch { } }
    }
}
