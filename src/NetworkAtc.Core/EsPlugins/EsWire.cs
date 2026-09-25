using System.Buffers.Binary;
using System.Text;

namespace NetworkAtc.Core.EsPlugins;

/// <summary>
/// Messages between Network-ATC and the EuroScope plugin host (native/EsBridge/src/Wire.h — keep both in step).
/// A frame is a 4-byte little-endian length, the message type byte and the fields in order.
/// </summary>
public enum EsMsg : byte
{
    Hello = 1, LoadPlugin = 2, UnloadPlugin = 3, Myself = 4, Controller = 5, ControllerGone = 6, Aircraft = 7, AircraftGone = 8,
    SectorReset = 9, SectorElement = 10, SectorDone = 11, Asel = 12, Command = 13, Chat = 14, Metar = 15, TagItemsInUse = 16,
    FunctionCall = 17, PopupSelect = 18, ViewOpen = 19, ViewClose = 20, ViewGeometry = 21, ViewRefresh = 22, ScreenObjectEvent = 23,
    ViewSave = 24, PlaneInfo = 25, Channels = 26, VoiceEvent = 27, FpListAction = 28, Quit = 29,

    Log = 64, PluginLoaded = 65, PluginFailed = 66, PluginUnloaded = 67, DisplayType = 68, TagItemType = 69, TagItemFunction = 70,
    UserMessage = 71, Action = 72, CommandResult = 73, TagValues = 74, PopupList = 75, PopupEdit = 76, ViewDrawn = 77, ViewData = 78,
    RequestRefresh = 79, StartTagFunction = 80, SetDisplayArea = 81, FpList = 82, ShowSectorElement = 83, Alias = 84, RefreshMap = 85,
    Ready = 86,
}

/// <summary>What a plugin asked Network-ATC to do.</summary>
public enum EsActionKind
{
    StartTracking = 1, EndTracking = 2, InitiateHandoff = 3, AcceptHandoff = 4, RefuseHandoff = 5, SetAssigned = 6, SetFlightPlan = 7,
    AmendFlightPlan = 8, SetAsel = 9, Correlate = 10, Uncorrelate = 11, InitiateCoordination = 12, AcceptCoordination = 13,
    RefuseCoordination = 14, PushStrip = 15, SetEstimation = 16, ClearEstimation = 17, SetAnnotation = 18, ChannelToggle = 19,
}

/// <summary>A rectangle in pixels of the radar view (left, top, right, bottom).</summary>
public readonly record struct EsRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public sealed class EsWriter
{
    private readonly MemoryStream _data = new();

    public EsWriter(EsMsg type)
    {
        _data.Write(new byte[4]);
        _data.WriteByte((byte)type);
    }

    public EsWriter U8(byte v) { _data.WriteByte(v); return this; }
    public EsWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public EsWriter Char(char c) => U8(c is > '\0' and < (char)128 ? (byte)c : (byte)' ');

    public EsWriter I32(int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        _data.Write(b);
        return this;
    }

    public EsWriter U32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        _data.Write(b);
        return this;
    }

    public EsWriter F64(double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        _data.Write(b);
        return this;
    }

    public EsWriter Str(string? s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        U32((uint)bytes.Length);
        _data.Write(bytes);
        return this;
    }

    public EsWriter Rect(EsRect r) => I32(r.Left).I32(r.Top).I32(r.Right).I32(r.Bottom);

    public byte[] Frame()
    {
        var bytes = _data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(bytes.Length - 4));
        return bytes;
    }
}

public sealed class EsReader(byte[] data, int offset = 0)
{
    private int _p = offset;

    public bool Ok { get; private set; } = true;

    private bool Need(int n)
    {
        if (!Ok || data.Length - _p < n) Ok = false;
        return Ok;
    }

    public byte U8() => Need(1) ? data[_p++] : (byte)0;
    public bool Bool() => U8() != 0;

    public int I32()
    {
        if (!Need(4)) return 0;
        int v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_p));
        _p += 4;
        return v;
    }

    public uint U32()
    {
        if (!Need(4)) return 0;
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_p));
        _p += 4;
        return v;
    }

    public double F64()
    {
        if (!Need(8)) return 0;
        double v = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(_p));
        _p += 8;
        return v;
    }

    public string Str()
    {
        uint n = U32();
        if (!Ok || n > data.Length - _p)
        {
            Ok = false;
            return "";
        }
        string s = Encoding.UTF8.GetString(data, _p, (int)n);
        _p += (int)n;
        return s;
    }

    public EsRect Rect() => new(I32(), I32(), I32(), I32());
}
