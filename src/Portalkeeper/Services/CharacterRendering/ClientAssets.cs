using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Portalkeeper.Services.CharacterRendering;

// Stock 3.3.5 archive precedence, established by the MPQ proof. Never writes client files.
internal sealed class ClientAssets : IDisposable
{
    private readonly List<IntPtr> _handles = new();
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationToken _token;
    private long _cacheBytes;
    public static string[] Archives(string clientPath)
    {
        string root = Path.Combine(Path.GetFullPath(clientPath), "Data");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose a valid WoW client folder in Settings.");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p).Equals(".mpq", StringComparison.OrdinalIgnoreCase)).ToArray();
        var map = files.ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\','/'), StringComparer.OrdinalIgnoreCase);
        var locales = new[] {"enUS","enGB","deDE","frFR","esES","esMX","ruRU","koKR","zhCN","zhTW"};
        var locale = locales.FirstOrDefault(l => map.ContainsKey($"{l}/locale-{l}.MPQ"))
            ?? throw new InvalidDataException("Client locale archives are unavailable.");
        // Do not silently ignore a custom patch and render a different appearance.
        if (map.Keys.Any(n => Path.GetFileName(n).StartsWith("patch", StringComparison.OrdinalIgnoreCase)
            && !new[] {"patch.MPQ","patch-2.MPQ","patch-3.MPQ",$"patch-{locale}.MPQ",$"patch-{locale}-2.MPQ",$"patch-{locale}-3.MPQ"}
                .Contains(Path.GetFileName(n), StringComparer.OrdinalIgnoreCase)))
            throw new NotSupportedException("Preview unavailable for this custom-patch client.");
        var order = new[] {$"{locale}/patch-{locale}-3.MPQ","patch-3.MPQ",$"{locale}/patch-{locale}-2.MPQ","patch-2.MPQ",$"{locale}/patch-{locale}.MPQ","patch.MPQ",$"{locale}/lichking-locale-{locale}.MPQ",$"{locale}/expansion-locale-{locale}.MPQ",$"{locale}/locale-{locale}.MPQ","lichking.MPQ","expansion.MPQ","common-2.MPQ","common.MPQ"};
        return order.Where(map.ContainsKey).Select(n => map[n]).ToArray();
    }
    public static string Fingerprint(string clientPath)
    {
        var files = Archives(clientPath);
        var text = string.Join("\n", files.Select(p => $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    public ClientAssets(string path, CancellationToken token)
    {
        _token = token;
        try
        {
            foreach (var archive in Archives(path))
            {
                token.ThrowIfCancellationRequested();
                if (!Storm.OpenArchive(archive, 0, 0x100, out var h)) throw new IOException("Cannot read client archive.");
                _handles.Add(h);
            }
        }
        catch { Dispose(); throw; }
    }
    public bool Exists(string path) => _handles.Any(h => Storm.HasFile(h, path));
    public byte[] Read(string path)
    {
        _token.ThrowIfCancellationRequested();
        path = path.Replace('/', '\\');
        if (_cache.TryGetValue(path, out var bytes)) return bytes;
        foreach (var archive in _handles)
        {
            if (!Storm.HasFile(archive, path)) continue;
            if (!Storm.OpenFile(archive, path, 0, out var file)) throw new IOException("Cannot read client asset: " + path);
            try
            {
                if (!Storm.GetInfo(file, 53, out var flags, 4, out _) || (flags & 0x02100000) != 0)
                    throw new NotSupportedException("Client delta patches are not supported by previews.");
                uint size = Storm.GetSize(file, out var high);
                if (high != 0 || size > 128 * 1024 * 1024) throw new InvalidDataException("Invalid client asset size.");
                bytes = new byte[size];
                if (!Storm.ReadFile(file, bytes, size, out var count, IntPtr.Zero) || count != size) throw new IOException("Incomplete client asset.");
                if (_cacheBytes + size > 96 * 1024 * 1024) { _cache.Clear(); _cacheBytes = 0; }
                _cache[path] = bytes; _cacheBytes += size;
                return bytes;
            }
            finally { Storm.CloseFile(file); }
        }
        throw new FileNotFoundException("Client asset unavailable: " + path);
    }
    public void Dispose() { foreach (var h in _handles) Storm.CloseArchive(h); _handles.Clear(); }
}

internal static class Storm
{
    private const string Library = "PortalkeeperStorm";
    static Storm()
    {
        NativeLibrary.SetDllImportResolver(typeof(Storm).Assembly, (name, assembly, search) =>
        {
            if (name != Library) return IntPtr.Zero;
            if (OperatingSystem.IsWindows())
            {
                if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new PlatformNotSupportedException("StormLib previews require Windows x64.");
                string nativePath = Path.Combine(AppContext.BaseDirectory, "StormLib.dll");
                if (!File.Exists(nativePath))
                    nativePath = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "StormLib.dll");
                return NativeLibrary.Load(nativePath);
            }
            foreach (var candidate in OperatingSystem.IsMacOS() ? new[] {"libstorm.dylib"} : new[] {"libstorm.so", "libstorm.so.9"})
                if (NativeLibrary.TryLoad(candidate, assembly, search, out var h)) return h;
            throw new DllNotFoundException("StormLib is unavailable. Install the native StormLib library to enable client previews.");
        });
    }
    internal static bool OpenArchive(string path, uint priority, uint flags, out IntPtr handle) =>
        OperatingSystem.IsWindows()
            ? OpenArchiveWindows(path, priority, flags, out handle)
            : OpenArchiveUnix(path, priority, flags, out handle);

    // The bundled official Windows DLL uses UTF-16 TCHAR paths; Unix uses UTF-8.
    [DllImport(Library, EntryPoint="SFileOpenArchive", ExactSpelling=true)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool OpenArchiveWindows([MarshalAs(UnmanagedType.LPWStr)] string path, uint priority, uint flags, out IntPtr handle);
    [DllImport(Library, EntryPoint="SFileOpenArchive", ExactSpelling=true)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool OpenArchiveUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint priority, uint flags, out IntPtr handle);
    [DllImport(Library, EntryPoint="SFileHasFile")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool HasFile(IntPtr archive,[MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library, EntryPoint="SFileOpenFileEx")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool OpenFile(IntPtr archive,[MarshalAs(UnmanagedType.LPUTF8Str)] string path,uint scope,out IntPtr file);
    [DllImport(Library, EntryPoint="SFileGetFileSize")] internal static extern uint GetSize(IntPtr file,out uint high);
    [DllImport(Library, EntryPoint="SFileReadFile")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool ReadFile(IntPtr file,[Out] byte[] buffer,uint size,out uint read,IntPtr overlapped);
    [DllImport(Library, EntryPoint="SFileGetFileInfo")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool GetInfo(IntPtr file,int info,out uint value,uint size,out uint needed);
    [DllImport(Library, EntryPoint="SFileCloseFile")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CloseFile(IntPtr file);
    [DllImport(Library, EntryPoint="SFileCloseArchive")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CloseArchive(IntPtr archive);
}

internal sealed class ClientDbc
{
    private readonly byte[] _data;
    private readonly int _strings;
    public uint[][] Rows { get; }
    private readonly Dictionary<uint,uint[]> _ids;
    public ClientDbc(ClientAssets assets,string name,int fields)
    {
        _data = assets.Read(@"DBFilesClient\" + name + ".dbc");
        if (_data.Length < 20 || Encoding.ASCII.GetString(_data,0,4) != "WDBC") throw new InvalidDataException("Invalid DBC.");
        uint n=BitConverter.ToUInt32(_data,4), f=BitConverter.ToUInt32(_data,8), w=BitConverter.ToUInt32(_data,12), z=BitConverter.ToUInt32(_data,16);
        if (f != fields || w != 4*f || 20L+n*(long)w+z != _data.Length) throw new InvalidDataException("Unsupported client DBC layout: " + name);
        _strings = checked(20+(int)(n*w));
        Rows = Enumerable.Range(0,(int)n).Select(i => Enumerable.Range(0,fields).Select(j => BitConverter.ToUInt32(_data,20+i*(int)w+j*4)).ToArray()).ToArray();
        _ids = Rows.GroupBy(r=>r[0]).ToDictionary(g=>g.Key,g=>g.First());
    }
    public uint[] Id(uint id) => _ids.TryGetValue(id,out var r) ? r : throw new InvalidDataException("Client display record unavailable.");
    public string Text(uint offset)
    {
        int p=checked(_strings+(int)offset);
        if (p < _strings || p >= _data.Length) throw new InvalidDataException("Invalid DBC string.");
        int end=Array.IndexOf(_data,(byte)0,p);
        if(end<0) throw new InvalidDataException("Invalid DBC string.");
        return Encoding.UTF8.GetString(_data,p,end-p);
    }
}
