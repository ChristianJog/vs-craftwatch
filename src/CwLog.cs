using System;
using System.IO;

namespace CraftWatch;

/// <summary>
/// Eigene Logdatei, damit die Aufzeichnung weder beim Weltstart ueberschrieben noch von der
/// Log-Archivierung des Spiels weggeraeumt wird. Client und Server schreiben im Singleplayer
/// in dieselbe Datei; die Seite steht in jeder Zeile.
/// </summary>
public static class CwLog
{
    private static readonly object gate = new();
    private static StreamWriter? writer;
    private static int users;

    public static string? FilePath { get; private set; }

    public static void Open(string dataPath)
    {
        lock (gate)
        {
            users++;
            if (writer != null) return;

            string dir = Path.Combine(dataPath, "CraftWatch");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, $"craftwatch-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            writer = new StreamWriter(FilePath, append: true) { AutoFlush = true };
        }
    }

    public static void Close()
    {
        lock (gate)
        {
            users--;
            if (users > 0 || writer == null) return;
            writer.Dispose();
            writer = null;
        }
    }

    public static void Write(string side, string kind, string message)
    {
        lock (gate)
        {
            writer?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{side}] {kind,-8} {message}");
        }
    }
}
