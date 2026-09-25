// The plugin host: keeps the world for the plugins, loads them, calls them on the main thread and talks
// to Network-ATC over the pipe. Everything that touches a plugin runs on the thread of Engine::Run.
#pragma once

#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "../include/EsPlugInApi.h"
#include "Records.h"

namespace natc
{
    class Engine;
}

/// One loaded plugin (the CPlugIn object points at it).
class CPlugInData
{
public:
    int Id = 0;
    std::string Path, Name, Version, Author, Copyright;
    int Compatibility = 0;
    EuroScopePlugIn::CPlugIn* Instance = nullptr;
    HMODULE Module = nullptr;
    bool Unloading = false;

    // Handles for the plugins (the interface classes let CPlugInData fill in their private pointers).
    static EuroScopePlugIn::CFlightPlan Fp(natc::Aircraft* a);
    static EuroScopePlugIn::CRadarTarget Rt(natc::Aircraft* a);
    static EuroScopePlugIn::CController Ctr(natc::Controller* c, bool myself = false);
    static EuroScopePlugIn::CSectorElement Element(int index, int type);
    static EuroScopePlugIn::CGrountToAirChannel ChannelHandle(int index);
    static EuroScopePlugIn::CFlightPlanList List(natc::FpList* list);

    static natc::Aircraft* Of(const EuroScopePlugIn::CFlightPlan& fp) { return static_cast<natc::Aircraft*>(fp.m_FpPosition); }
    static natc::Aircraft* Of(const EuroScopePlugIn::CRadarTarget& rt) { return static_cast<natc::Aircraft*>(rt.m_RtPosition); }
    static natc::Controller* Of(const EuroScopePlugIn::CController& c) { return static_cast<natc::Controller*>(c.m_CtrPosition); }
    static bool IsMyself(const EuroScopePlugIn::CController& c) { return c.m_Myself; }
    static int IndexOf(const EuroScopePlugIn::CSectorElement& e) { return e.m_Position; }
    static int IndexOf(const EuroScopePlugIn::CGrountToAirChannel& c) { return c.m_Index; }
    static natc::FpList* Of(const EuroScopePlugIn::CFlightPlanList& l) { return static_cast<natc::FpList*>(l.m_Position); }
    static CPlugInData* Of(const EuroScopePlugIn::CPlugIn* p) { return p->m_pPluginData; }
    static void SetData(EuroScopePlugIn::CPlugIn* p, CPlugInData* d) { p->m_pPluginData = d; }
    static void Attach(EuroScopePlugIn::CRadarScreen* s, CRadarView* v, EuroScopePlugIn::CPlugIn* p)
    {
        s->m_pRadarView = v;
        s->m_pPlugIn = p;
    }
    static CRadarView* ViewOf(EuroScopePlugIn::CRadarScreen* s) { return s->m_pRadarView; }
};

/// A radar display of Network-ATC with the plugin screens drawn on it.
class CRadarView
{
public:
    struct Screen
    {
        EuroScopePlugIn::CRadarScreen* Object = nullptr;
        CPlugInData* Plugin = nullptr;
        bool Alive = true;
    };

    int Id = 0;
    std::string DisplayType;
    int Width = 0, Height = 0;
    double ProjectionLatitude = 0, ProjectionLongitude = 0, CenterX = 0, CenterY = 0, NmPerPixel = 0.1;
    uint32_t KeyColor = 0;
    natc::Rect RadarArea, ToolbarArea, ChatArea;
    std::vector<Screen> Screens;
    std::map<std::string, std::pair<std::string, std::string>> Asr;  // name → (description, value)
    std::vector<natc::ScreenObject> Objects, Building;

    // Drawing: two shared bitmaps (under and over the tags), recreated when the size changes.
    struct Layer
    {
        HANDLE Mapping = nullptr;
        HBITMAP Bitmap = nullptr;
        HDC Dc = nullptr;
        void* Bits = nullptr;
        std::string Name;
    };
    Layer Back, Front;
    int Generation = 0;

    ~CRadarView();

    POINT ToPixel(double latitude, double longitude) const;
    void ToPosition(POINT pt, double& latitude, double& longitude) const;
    void EnsureLayers();
    void FreeLayers();
};

namespace natc
{
    struct PopupElement
    {
        std::string S1, S2;
        int FunctionId = 0, Checked = 2;
        bool Selected = false, Disabled = false, Fixed = false;
    };

    /// Where the answer to a popup goes: a plugin, or one of its radar screens.
    struct PopupTarget
    {
        CPlugInData* Plugin = nullptr;
        int ViewId = 0, ScreenIndex = -1;
    };

    class Engine
    {
    public:
        static Engine& Get();

        int Run(const std::string& pipeName);

        // ---- world ----
        std::map<std::string, std::unique_ptr<Aircraft>> AircraftByCallsign;  // upper-case callsign
        std::vector<Aircraft*> AircraftOrder;
        Controller Myself;
        std::map<std::string, std::unique_ptr<Controller>> ControllersByCallsign;
        std::vector<Controller*> ControllerOrder;
        std::vector<std::vector<SectorElement>> Elements = std::vector<std::vector<SectorElement>>(20);
        std::vector<Channel> Channels;
        std::vector<std::unique_ptr<FpList>> Lists;
        std::string Asel, SectorFileName;
        int TransitionAltitude = 6000, ConnectionType = 0;

        Aircraft* FindAircraft(const char* callsign, bool includeGone = false);
        Controller* FindController(const char* callsign);
        Controller* FindControllerById(const char* positionId);

        // ---- plugins ----
        std::vector<std::unique_ptr<CPlugInData>> Plugins;
        CPlugInData* Loading = nullptr;  // the plugin whose CPlugIn is being constructed
        std::map<std::string, std::map<std::string, std::pair<std::string, std::string>>> Settings;  // plugin → name → (desc, value)
        std::string SettingsFile;
        void SaveSettings();

        std::map<int, std::unique_ptr<CRadarView>> Views;
        std::set<std::pair<int, int>> TagItemsInUse;

        // ---- calling into plugins ----
        /// Calls f for every live plugin, catching whatever a plugin throws (access violations included).
        void EachPlugin(const std::function<void(CPlugInData&)>& f);
        /// Calls f for every live screen of every view.
        void EachScreen(const std::function<void(CRadarView&, int, EuroScopePlugIn::CRadarScreen&)>& f);
        bool Guard(CPlugInData* plugin, const char* what, const std::function<void()>& f);

        // The context of the call in progress: popups opened now answer to this plugin/screen.
        CPlugInData* ContextPlugin = nullptr;
        CRadarView* ContextView = nullptr;
        int ContextScreen = -1;

        // ---- popups ----
        bool PopupOpen = false, PopupIsEdit = false;
        std::string PopupTitle, PopupInitial;
        int PopupColumns = 1, PopupFunction = 0;
        Rect PopupArea;
        std::vector<PopupElement> PopupElements;
        PopupTarget PopupOwner;
        std::map<int, PopupTarget> Popups;
        int NextPopupId = 1;
        void FlushPopup();

        // ---- to Network-ATC ----
        void Send(Writer& w);
        void Log(const std::string& text, bool error = false);
        void Act(ActionKind kind, const Aircraft* a, const std::string& x = "", const std::string& y = "", int n = 0);
        void SendDisplayType(CPlugInData* p, const char* name, bool needRadar, bool geo, bool save, bool create);
        void MarkListChanged(FpList* list);

        // ---- events to the plugins after a change ----
        void FireFlightPlanDataUpdate(Aircraft* a);
        void FireAssignedDataUpdate(Aircraft* a, int dataType);
        void FireTargetUpdate(Aircraft* a);

    private:
        Engine() = default;
        void Handle(const std::vector<uint8_t>& frame);
        void OnTimer();
        void LoadPlugin(const std::string& path);
        void UnloadPlugin(CPlugInData* p);
        void OpenView(int id, const std::string& type, std::vector<std::pair<std::string, std::string>> data);
        void CloseView(int id);
        void RefreshView(CRadarView& v);
        void ReadAircraft(Reader& r);
        static void ReadController(Reader& r, Controller& c);
        void SendTagValues(bool all);
        void SendLists();
        void LoadSettings();

        HANDLE m_Pipe = nullptr;
        bool PipeIo(bool write, uint8_t* data, DWORD length, DWORD& done);
        std::mutex m_WriteLock;
        int m_TimerCount = 0;
        int m_NextPluginId = 1;
        int m_NextListId = 1;

    public:
        HWND Window = nullptr;
        std::mutex QueueLock;
        std::vector<std::vector<uint8_t>> Queue;
        void Pump();
        int NextListId() { return m_NextListId++; }
    };
}
