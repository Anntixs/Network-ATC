// Messages between Network-ATC (C#, 64-bit) and the plugin host (this DLL in a 32-bit process), over a
// named pipe. A frame is: u32 length of what follows, u8 message type, then the fields in order.
// Integers are little-endian, doubles IEEE 754, strings u32 length + UTF-8 bytes, bools one byte.
// The C# side mirrors this file in NetworkAtc.Core/EsPlugins/EsWire.cs.
#pragma once

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace natc
{
    enum class Msg : uint8_t
    {
        // ---- Network-ATC → host ----
        Hello = 1,              // str settingsFile, i32 transitionAltitude
        LoadPlugin = 2,         // str path
        UnloadPlugin = 3,       // i32 pluginId
        Myself = 4,             // ControllerFields, i32 connectionType
        Controller = 5,         // ControllerFields
        ControllerGone = 6,     // str callsign
        Aircraft = 7,           // AircraftFields
        AircraftGone = 8,       // str callsign
        SectorReset = 9,        // str sectorFileName
        SectorElement = 10,     // i32 type, str name, str airport, f64 frequency, i32 n{f64 lat, f64 lon}, i32 n{str component},
                                // str rwy1, str rwy2, i32 hdg1, i32 hdg2, bool dep1, bool arr1, bool dep2, bool arr2
        SectorDone = 11,        // (none) — elements complete, OnAirportRunwayActivityChanged follows
        Asel = 12,              // str callsign
        Command = 13,           // i32 requestId, str text → CommandResult
        Chat = 14,              // bool private, str sender, str receiver, f64 frequency, str text
        Metar = 15,             // str station, str metar
        TagItemsInUse = 16,     // i32 n{i32 pluginId, i32 code}
        FunctionCall = 17,      // i32 pluginId, i32 functionId, str callsign, str itemString, i32 x, i32 y, Rect area
        PopupSelect = 18,       // i32 popupId, i32 functionId, str text, i32 x, i32 y, Rect area
        ViewOpen = 19,          // i32 viewId, str displayType, i32 n{str name, str value} (the view's saved data)
        ViewClose = 20,         // i32 viewId
        ViewGeometry = 21,      // i32 viewId, i32 width, i32 height, f64 projLat, f64 projLon, f64 cx, f64 cy, f64 nmPerPixel,
                                // u32 keyColor (0x00BBGGRR), Rect radarArea, Rect toolbarArea, Rect chatArea
        ViewRefresh = 22,       // i32 viewId → ViewDrawn
        ScreenObjectEvent = 23, // i32 viewId, i32 screenIndex, i32 event, i32 objectType, str objectId, i32 x, i32 y, Rect area, i32 buttonOrReleased
        ViewSave = 24,          // i32 viewId — OnAsrContentToBeSaved; values come back as ViewData
        PlaneInfo = 25,         // str callsign, str livery, str type
        Channels = 26,          // i32 n{str name, f64 freq, bool primary, bool atis, bool txtRx, bool txtTx, bool vRx, bool vTx, bool vConnected}
        VoiceEvent = 27,        // i32 kind (0 tx start, 1 tx end, 2 rx start, 3 rx end), bool primary, i32 channel
        FpListAction = 28,      // i32 listId, i32 kind (0 refresh now)
        Quit = 29,

        // ---- host → Network-ATC ----
        Log = 64,               // bool error, str text
        PluginLoaded = 65,      // i32 pluginId, str path, str name, str version, str author, str copyright
        PluginFailed = 66,      // str path, str reason
        PluginUnloaded = 67,    // i32 pluginId
        DisplayType = 68,       // i32 pluginId, str name, bool needRadarContent, bool geoReferenced, bool canBeSaved, bool canBeCreated
        TagItemType = 69,       // i32 pluginId, str name, i32 code
        TagItemFunction = 70,   // i32 pluginId, str name, i32 code
        UserMessage = 71,       // str handler, str sender, str text, bool showHandler, bool unread, bool unreadEvenIfBusy, bool flash, bool confirm
        Action = 72,            // i32 kind, str callsign, str a, str b, i32 n
        CommandResult = 73,     // i32 requestId, bool handled
        TagValues = 74,         // i32 n{str callsign, i32 pluginId, i32 code, str text, i32 colorCode, u32 rgb, f64 fontSize}
        PopupList = 75,         // i32 popupId, i32 pluginId, str title, i32 columns, Rect area, i32 n{str s1, str s2, i32 fid, bool selected, i32 checked, bool disabled, bool fixed}
        PopupEdit = 76,         // i32 popupId, i32 pluginId, i32 functionId, Rect area, str initial
        ViewDrawn = 77,         // i32 viewId, str backMapping, str frontMapping, i32 width, i32 height, bool drewAnything,
                                // i32 n{i32 screenIndex, i32 objectType, str objectId, Rect area, bool moveable, str message}
        ViewData = 78,          // i32 viewId, str name, str description, str value
        RequestRefresh = 79,    // i32 viewId
        StartTagFunction = 80,  // str callsign, str itemPlugin, i32 itemCode, str itemString, str functionPlugin, i32 functionId, i32 x, i32 y, Rect area
        SetDisplayArea = 81,    // i32 viewId, f64 lat1, f64 lon1, f64 lat2, f64 lon2
        FpList = 82,            // i32 listId, i32 pluginId, str name, bool visible, i32 n{str title, i32 width, bool centered, str itemPlugin, i32 itemCode,
                                //  str leftPlugin, i32 leftFunction, str rightPlugin, i32 rightFunction}, i32 n{str callsign}
        ShowSectorElement = 83, // i32 viewId, i32 type, str name, str component, bool show
        Alias = 84,             // str name, str value
        RefreshMap = 85,        // i32 viewId
        Ready = 86,             // (none) — the host is up
    };

    enum class ActionKind : int32_t
    {
        StartTracking = 1, EndTracking = 2, InitiateHandoff = 3, AcceptHandoff = 4, RefuseHandoff = 5,
        SetAssigned = 6,       // n = CTR_DATA_TYPE_*, a = value
        SetFlightPlan = 7,     // a = field name, b = value
        AmendFlightPlan = 8,
        SetAsel = 9,
        Correlate = 10, Uncorrelate = 11,
        InitiateCoordination = 12, AcceptCoordination = 13, RefuseCoordination = 14,  // a = controller, b = point, n = altitude
        PushStrip = 15,        // a = target controller
        SetEstimation = 16,    // a = point, b = time
        ClearEstimation = 17,  // a = point or ""
        SetAnnotation = 18,    // n = index, a = text
        ChannelToggle = 19,    // n = channel index, a = what (primary, atis, textrx, texttx, voicerx, voicetx)
    };

    struct Rect
    {
        int32_t left = 0, top = 0, right = 0, bottom = 0;
    };

    class Writer
    {
    public:
        explicit Writer(Msg type)
        {
            m_Data.resize(4);
            U8(static_cast<uint8_t>(type));
        }

        void U8(uint8_t v) { m_Data.push_back(v); }
        void Bool(bool v) { U8(v ? 1 : 0); }
        void I32(int32_t v) { Raw(&v, 4); }
        void U32(uint32_t v) { Raw(&v, 4); }
        void F64(double v) { Raw(&v, 8); }
        void Str(const std::string& s)
        {
            U32(static_cast<uint32_t>(s.size()));
            Raw(s.data(), s.size());
        }
        void R(const Rect& r)
        {
            I32(r.left);
            I32(r.top);
            I32(r.right);
            I32(r.bottom);
        }

        /// The frame with its length filled in.
        const std::vector<uint8_t>& Frame()
        {
            uint32_t n = static_cast<uint32_t>(m_Data.size() - 4);
            std::memcpy(m_Data.data(), &n, 4);
            return m_Data;
        }

    private:
        void Raw(const void* p, size_t n)
        {
            auto b = static_cast<const uint8_t*>(p);
            m_Data.insert(m_Data.end(), b, b + n);
        }

        std::vector<uint8_t> m_Data;
    };

    class Reader
    {
    public:
        Reader(const uint8_t* data, size_t size) : m_P(data), m_End(data + size) {}

        bool Ok() const { return m_Ok; }
        uint8_t U8()
        {
            uint8_t v = 0;
            Raw(&v, 1);
            return v;
        }
        bool Bool() { return U8() != 0; }
        int32_t I32()
        {
            int32_t v = 0;
            Raw(&v, 4);
            return v;
        }
        uint32_t U32()
        {
            uint32_t v = 0;
            Raw(&v, 4);
            return v;
        }
        double F64()
        {
            double v = 0;
            Raw(&v, 8);
            return v;
        }
        std::string Str()
        {
            uint32_t n = U32();
            if (!m_Ok || n > static_cast<size_t>(m_End - m_P))
            {
                m_Ok = false;
                return {};
            }
            std::string s(reinterpret_cast<const char*>(m_P), n);
            m_P += n;
            return s;
        }
        Rect R()
        {
            Rect r;
            r.left = I32();
            r.top = I32();
            r.right = I32();
            r.bottom = I32();
            return r;
        }

    private:
        void Raw(void* out, size_t n)
        {
            if (!m_Ok || static_cast<size_t>(m_End - m_P) < n)
            {
                m_Ok = false;
                return;
            }
            std::memcpy(out, m_P, n);
            m_P += n;
        }

        const uint8_t* m_P;
        const uint8_t* m_End;
        bool m_Ok = true;
    };
}
