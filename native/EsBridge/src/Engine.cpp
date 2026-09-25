#include "Engine.h"

#include <eh.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <fstream>
#include <sstream>
#include <stdexcept>
#include <thread>

using namespace EuroScopePlugIn;

namespace natc
{
    namespace
    {
        constexpr UINT WM_NATC_PACKET = WM_APP + 1;
        constexpr double Rad = 3.14159265358979323846 / 180.0;
        constexpr double EarthRadiusNm = 3440.065;

        std::string Upper(std::string s)
        {
            for (auto& c : s) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
            return s;
        }

        // Access violations and other structured exceptions inside a plugin become C++ exceptions (built with /EHa),
        // so one broken plugin does not take the host down.
        struct SehError : std::runtime_error
        {
            explicit SehError(unsigned code) : std::runtime_error("exception 0x" + [code] {
                char b[16];
                snprintf(b, sizeof b, "%08X", code);
                return std::string(b);
            }()) {}
        };

        void Translate(unsigned code, EXCEPTION_POINTERS*) { throw SehError(code); }

        LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
        {
            if (msg == WM_NATC_PACKET)
            {
                Engine::Get().Pump();
                return 0;
            }
            return DefWindowProcA(hwnd, msg, wp, lp);
        }
    }

    std::string ToAnsi(const std::string& utf8)
    {
        if (utf8.empty()) return {};
        int n = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
        std::wstring w(static_cast<size_t>(n), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), w.data(), n);
        int m = WideCharToMultiByte(CP_ACP, 0, w.data(), n, nullptr, 0, nullptr, nullptr);
        std::string a(static_cast<size_t>(m), '\0');
        WideCharToMultiByte(CP_ACP, 0, w.data(), n, a.data(), m, nullptr, nullptr);
        return a;
    }

    std::string ToUtf8(const char* ansi)
    {
        if (ansi == nullptr || *ansi == 0) return {};
        int n = MultiByteToWideChar(CP_ACP, 0, ansi, -1, nullptr, 0);
        std::wstring w(static_cast<size_t>(n), L'\0');
        MultiByteToWideChar(CP_ACP, 0, ansi, -1, w.data(), n);
        int m = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string u(static_cast<size_t>(m), '\0');
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, u.data(), m, nullptr, nullptr);
        if (!u.empty() && u.back() == '\0') u.pop_back();
        return u;
    }

    Engine& Engine::Get()
    {
        static Engine engine;
        return engine;
    }

    // ---- world ----------------------------------------------------------------------------------

    Aircraft* Engine::FindAircraft(const char* callsign, bool includeGone)
    {
        if (callsign == nullptr) return nullptr;
        auto it = AircraftByCallsign.find(Upper(callsign));
        if (it == AircraftByCallsign.end() || (it->second->Gone && !includeGone)) return nullptr;
        return it->second.get();
    }

    Controller* Engine::FindController(const char* callsign)
    {
        if (callsign == nullptr) return nullptr;
        auto it = ControllersByCallsign.find(Upper(callsign));
        return it == ControllersByCallsign.end() || it->second->Gone ? nullptr : it->second.get();
    }

    Controller* Engine::FindControllerById(const char* positionId)
    {
        if (positionId == nullptr) return nullptr;
        for (auto* c : ControllerOrder)
            if (!c->Gone && _stricmp(c->PositionId.c_str(), positionId) == 0) return c;
        return nullptr;
    }

    // ---- calling plugins ------------------------------------------------------------------------

    bool Engine::Guard(CPlugInData* plugin, const char* what, const std::function<void()>& f)
    {
        try
        {
            f();
            return true;
        }
        catch (const std::exception& e)
        {
            Log((plugin ? plugin->Name : std::string("?")) + ": error in " + what + ": " + e.what(), true);
        }
        catch (...)
        {
            Log((plugin ? plugin->Name : std::string("?")) + ": error in " + what, true);
        }
        return false;
    }

    void Engine::EachPlugin(const std::function<void(CPlugInData&)>& f)
    {
        for (size_t i = 0; i < Plugins.size(); i++)
        {
            auto* p = Plugins[i].get();
            if (p->Instance == nullptr || p->Unloading) continue;
            ContextPlugin = p;
            f(*p);
        }
        ContextPlugin = nullptr;
    }

    void Engine::EachScreen(const std::function<void(CRadarView&, int, CRadarScreen&)>& f)
    {
        for (auto& [id, view] : Views)
            for (size_t i = 0; i < view->Screens.size(); i++)
            {
                auto& s = view->Screens[i];
                if (!s.Alive || s.Object == nullptr) continue;
                ContextPlugin = s.Plugin;
                ContextView = view.get();
                ContextScreen = static_cast<int>(i);
                f(*view, static_cast<int>(i), *s.Object);
            }
        ContextPlugin = nullptr;
        ContextView = nullptr;
        ContextScreen = -1;
    }

    // ---- sending --------------------------------------------------------------------------------

    void Engine::Send(Writer& w)
    {
        const auto& frame = w.Frame();
        std::lock_guard<std::mutex> lock(m_WriteLock);
        if (m_Pipe == nullptr) return;
        DWORD total = 0;
        while (total < frame.size())
        {
            DWORD written = 0;
            if (!PipeIo(true, const_cast<uint8_t*>(frame.data()) + total, static_cast<DWORD>(frame.size()) - total, written) || written == 0) return;
            total += written;
        }
    }

    // The pipe is opened for overlapped I/O: on a synchronous handle a pending read on the
    // reader thread would block every write from the main thread.
    bool Engine::PipeIo(bool write, uint8_t* data, DWORD length, DWORD& done)
    {
        OVERLAPPED ov{};
        ov.hEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
        if (ov.hEvent == nullptr) return false;
        BOOL ok = write ? WriteFile(m_Pipe, data, length, nullptr, &ov) : ReadFile(m_Pipe, data, length, nullptr, &ov);
        bool result = true;
        if (!ok && GetLastError() != ERROR_IO_PENDING)
            result = false;
        else
            result = GetOverlappedResult(m_Pipe, &ov, &done, TRUE) != FALSE;
        CloseHandle(ov.hEvent);
        return result;
    }

    void Engine::Log(const std::string& text, bool error)
    {
        Writer w(Msg::Log);
        w.Bool(error);
        w.Str(text);
        Send(w);
    }

    void Engine::Act(ActionKind kind, const Aircraft* a, const std::string& x, const std::string& y, int n)
    {
        Writer w(Msg::Action);
        w.I32(static_cast<int32_t>(kind));
        w.Str(a ? a->Callsign : std::string());
        w.Str(ToUtf8(x.c_str()));
        w.Str(ToUtf8(y.c_str()));
        w.I32(n);
        Send(w);
    }

    void Engine::SendDisplayType(CPlugInData* p, const char* name, bool needRadar, bool geo, bool save, bool create)
    {
        Writer w(Msg::DisplayType);
        w.I32(p ? p->Id : 0);
        w.Str(ToUtf8(name));
        w.Bool(needRadar);
        w.Bool(geo);
        w.Bool(save);
        w.Bool(create);
        Send(w);
    }

    void Engine::MarkListChanged(FpList* list)
    {
        if (list) list->Changed = true;
    }

    void Engine::FlushPopup()
    {
        if (!PopupOpen) return;
        PopupOpen = false;
        int id = NextPopupId++;
        Popups[id] = PopupOwner;
        if (Popups.size() > 64) Popups.erase(Popups.begin());
        if (PopupIsEdit)
        {
            Writer w(Msg::PopupEdit);
            w.I32(id);
            w.I32(PopupOwner.Plugin ? PopupOwner.Plugin->Id : 0);
            w.I32(PopupFunction);
            w.R(PopupArea);
            w.Str(ToUtf8(PopupInitial.c_str()));
            Send(w);
            return;
        }
        Writer w(Msg::PopupList);
        w.I32(id);
        w.I32(PopupOwner.Plugin ? PopupOwner.Plugin->Id : 0);
        w.Str(ToUtf8(PopupTitle.c_str()));
        w.I32(PopupColumns);
        w.R(PopupArea);
        w.I32(static_cast<int32_t>(PopupElements.size()));
        for (auto& e : PopupElements)
        {
            w.Str(ToUtf8(e.S1.c_str()));
            w.Str(ToUtf8(e.S2.c_str()));
            w.I32(e.FunctionId);
            w.Bool(e.Selected);
            w.I32(e.Checked);
            w.Bool(e.Disabled);
            w.Bool(e.Fixed);
        }
        Send(w);
        PopupElements.clear();
    }

    // ---- events ---------------------------------------------------------------------------------

    void Engine::FireFlightPlanDataUpdate(Aircraft* a)
    {
        auto fp = CPlugInData::Fp(a);
        EachPlugin([&](CPlugInData& p) { Guard(&p, "OnFlightPlanFlightPlanDataUpdate", [&] { p.Instance->OnFlightPlanFlightPlanDataUpdate(fp); }); });
        EachScreen([&](CRadarView&, int, CRadarScreen& s) { Guard(ContextPlugin, "OnFlightPlanFlightPlanDataUpdate", [&] { s.OnFlightPlanFlightPlanDataUpdate(fp); }); });
    }

    void Engine::FireAssignedDataUpdate(Aircraft* a, int dataType)
    {
        auto fp = CPlugInData::Fp(a);
        EachPlugin([&](CPlugInData& p) {
            Guard(&p, "OnFlightPlanControllerAssignedDataUpdate", [&] { p.Instance->OnFlightPlanControllerAssignedDataUpdate(fp, dataType); });
        });
        EachScreen([&](CRadarView&, int, CRadarScreen& s) {
            Guard(ContextPlugin, "OnFlightPlanControllerAssignedDataUpdate", [&] { s.OnFlightPlanControllerAssignedDataUpdate(fp, dataType); });
        });
    }

    void Engine::FireTargetUpdate(Aircraft* a)
    {
        auto rt = CPlugInData::Rt(a);
        EachPlugin([&](CPlugInData& p) { Guard(&p, "OnRadarTargetPositionUpdate", [&] { p.Instance->OnRadarTargetPositionUpdate(rt); }); });
        EachScreen([&](CRadarView&, int, CRadarScreen& s) { Guard(ContextPlugin, "OnRadarTargetPositionUpdate", [&] { s.OnRadarTargetPositionUpdate(rt); }); });
    }

    // ---- the main loop --------------------------------------------------------------------------

    int Engine::Run(const std::string& pipeName)
    {
        _set_se_translator(Translate);
        std::string path = "\\\\.\\pipe\\" + pipeName;
        for (int attempt = 0; attempt < 100 && m_Pipe == nullptr; attempt++)
        {
            HANDLE h = CreateFileA(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
            if (h != INVALID_HANDLE_VALUE)
                m_Pipe = h;
            else
                Sleep(100);
        }
        if (m_Pipe == nullptr) return 2;

        WNDCLASSA wc{};
        wc.lpfnWndProc = WindowProc;
        wc.hInstance = GetModuleHandleA(nullptr);
        wc.lpszClassName = "NatcEsHost";
        RegisterClassA(&wc);
        Window = CreateWindowExA(0, wc.lpszClassName, "Network-ATC plugin host", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
        SetTimer(Window, 1, 1000, nullptr);

        // The reader thread only collects frames; everything else happens on this thread.
        std::thread reader([this] {
            std::vector<uint8_t> frame;
            for (;;)
            {
                uint32_t length = 0;
                DWORD got = 0, total = 0;
                while (total < 4)
                {
                    if (!PipeIo(false, reinterpret_cast<uint8_t*>(&length) + total, 4 - total, got) || got == 0) goto closed;
                    total += got;
                }
                if (length > 256u * 1024 * 1024) goto closed;
                frame.assign(length, 0);
                total = 0;
                while (total < length)
                {
                    if (!PipeIo(false, frame.data() + total, length - total, got) || got == 0) goto closed;
                    total += got;
                }
                {
                    std::lock_guard<std::mutex> lock(QueueLock);
                    Queue.push_back(frame);
                }
                PostMessageA(Window, WM_NATC_PACKET, 0, 0);
            }
        closed:
            PostMessageA(Window, WM_QUIT, 0, 0);
        });
        reader.detach();

        Writer ready(Msg::Ready);
        Send(ready);

        MSG msg;
        while (GetMessageA(&msg, nullptr, 0, 0) > 0)
        {
            if (msg.hwnd == Window && msg.message == WM_TIMER)
            {
                OnTimer();
                continue;
            }
            if (msg.hwnd == Window && msg.message == WM_QUIT) break;
            TranslateMessage(&msg);
            DispatchMessageA(&msg);
        }

        for (auto& p : Plugins)
            if (p->Instance != nullptr) UnloadPlugin(p.get());
        SaveSettings();
        return 0;
    }

    void Engine::Pump()
    {
        std::vector<std::vector<uint8_t>> frames;
        {
            std::lock_guard<std::mutex> lock(QueueLock);
            frames.swap(Queue);
        }
        for (auto& f : frames) Handle(f);
    }

    void Engine::OnTimer()
    {
        m_TimerCount++;
        int counter = m_TimerCount;
        EachPlugin([&](CPlugInData& p) { Guard(&p, "OnTimer", [&] { p.Instance->OnTimer(counter); }); });
        FlushPopup();
        for (auto& list : Lists)
        {
            CPlugInData* owner = nullptr;
            for (auto& p : Plugins)
                if (p->Id == list->PluginId) owner = p.get();
            if (owner == nullptr || owner->Instance == nullptr || owner->Unloading) continue;
            auto handle = CPlugInData::List(list.get());
            ContextPlugin = owner;
            Guard(owner, "OnRefreshFpListContent", [&] { owner->Instance->OnRefreshFpListContent(handle); });
            ContextPlugin = nullptr;
        }
        SendLists();
        SendTagValues(false);
        if (m_TimerCount % 30 == 0) SaveSettings();
    }

    // ---- plugins --------------------------------------------------------------------------------

    void Engine::LoadPlugin(const std::string& utf8Path)
    {
        std::string path = ToAnsi(utf8Path);
        auto fail = [&](const std::string& reason) {
            Writer w(Msg::PluginFailed);
            w.Str(utf8Path);
            w.Str(reason);
            Send(w);
        };
        for (auto& p : Plugins)
            if (p->Instance != nullptr && _stricmp(p->Path.c_str(), path.c_str()) == 0) return fail("plugin already loaded");

        HMODULE module = LoadLibraryExA(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == nullptr)
        {
            DWORD e = GetLastError();
            return fail(e == ERROR_BAD_EXE_FORMAT ? "not a 32-bit DLL" : e == ERROR_MOD_NOT_FOUND ? "a DLL the plugin depends on was not found (e.g. the Visual C++ x86 redistributable)" : "Windows could not load the DLL, code " + std::to_string(e));
        }
        using InitFn = void (*)(CPlugIn**);
        auto init = reinterpret_cast<InitFn>(GetProcAddress(module, "?EuroScopePlugInInit@@YAXPAPAVCPlugIn@EuroScopePlugIn@@@Z"));
        if (init == nullptr) init = reinterpret_cast<InitFn>(GetProcAddress(module, "EuroScopePlugInInit"));
        if (init == nullptr)
        {
            FreeLibrary(module);
            return fail("the DLL has no EuroScopePlugInInit, it is not a EuroScope plugin");
        }

        auto data = std::make_unique<CPlugInData>();
        data->Id = m_NextPluginId++;
        data->Path = path;
        data->Module = module;
        Loading = data.get();
        Plugins.push_back(std::move(data));
        CPlugInData* p = Plugins.back().get();
        CPlugIn* instance = nullptr;
        ContextPlugin = p;
        bool ok = Guard(p, "EuroScopePlugInInit", [&] { init(&instance); });
        ContextPlugin = nullptr;
        Loading = nullptr;
        if (!ok || instance == nullptr)
        {
            p->Unloading = true;
            return fail("the plugin failed to start (EuroScopePlugInInit)");
        }
        p->Instance = instance;
        if (p->Name.empty()) p->Name = CPlugInData::Of(instance) ? CPlugInData::Of(instance)->Name : path;

        Writer w(Msg::PluginLoaded);
        w.I32(p->Id);
        w.Str(utf8Path);
        w.Str(ToUtf8(p->Name.c_str()));
        w.Str(ToUtf8(p->Version.c_str()));
        w.Str(ToUtf8(p->Author.c_str()));
        w.Str(ToUtf8(p->Copyright.c_str()));
        Send(w);

        // The new plugin sees the displays that are already open.
        for (auto& entry : Views)
        {
            auto& view = entry.second;
            ContextPlugin = p;
            CRadarScreen* s = nullptr;
            Guard(p, "OnRadarScreenCreated", [&] { s = instance->OnRadarScreenCreated(view->DisplayType.c_str(), true, true, true, true); });
            if (s == nullptr) continue;
            CPlugInData::Attach(s, view.get(), instance);
            view->Screens.push_back({s, p, true});
            ContextView = view.get();
            ContextScreen = static_cast<int>(view->Screens.size() - 1);
            Guard(p, "OnAsrContentLoaded", [&] { s->OnAsrContentLoaded(true); });
        }
        ContextPlugin = nullptr;
        ContextView = nullptr;
        ContextScreen = -1;
        FlushPopup();
        SendTagValues(true);
    }

    void Engine::UnloadPlugin(CPlugInData* p)
    {
        if (p->Instance == nullptr || p->Unloading) return;
        for (auto& [id, view] : Views)
            for (size_t i = 0; i < view->Screens.size(); i++)
            {
                auto& s = view->Screens[i];
                if (!s.Alive || s.Plugin != p) continue;
                s.Alive = false;
                ContextPlugin = p;
                Guard(p, "OnAsrContentToBeClosed", [&] { s.Object->OnAsrContentToBeClosed(); });
                s.Object = nullptr;
            }
        p->Unloading = true;
        using ExitFn = void (*)();
        auto exitFn = reinterpret_cast<ExitFn>(GetProcAddress(p->Module, "?EuroScopePlugInExit@@YAXXZ"));
        if (exitFn == nullptr) exitFn = reinterpret_cast<ExitFn>(GetProcAddress(p->Module, "EuroScopePlugInExit"));
        ContextPlugin = p;
        if (exitFn) Guard(p, "EuroScopePlugInExit", [&] { exitFn(); });
        ContextPlugin = nullptr;
        p->Instance = nullptr;
        FreeLibrary(p->Module);
        p->Module = nullptr;
        Writer w(Msg::PluginUnloaded);
        w.I32(p->Id);
        Send(w);
    }

    // ---- views ----------------------------------------------------------------------------------

    void Engine::OpenView(int id, const std::string& type, std::vector<std::pair<std::string, std::string>> data)
    {
        CloseView(id);
        auto view = std::make_unique<CRadarView>();
        view->Id = id;
        view->DisplayType = ToAnsi(type);
        for (auto& [name, value] : data) view->Asr[ToAnsi(name)] = {"", ToAnsi(value)};
        CRadarView* v = view.get();
        Views[id] = std::move(view);
        EachPlugin([&](CPlugInData& p) {
            CRadarScreen* s = nullptr;
            Guard(&p, "OnRadarScreenCreated", [&] { s = p.Instance->OnRadarScreenCreated(v->DisplayType.c_str(), true, true, true, true); });
            if (s == nullptr) return;
            CPlugInData::Attach(s, v, p.Instance);
            v->Screens.push_back({s, &p, true});
        });
        for (size_t i = 0; i < v->Screens.size(); i++)
        {
            auto& s = v->Screens[i];
            ContextPlugin = s.Plugin;
            ContextView = v;
            ContextScreen = static_cast<int>(i);
            Guard(s.Plugin, "OnAsrContentLoaded", [&] { s.Object->OnAsrContentLoaded(true); });
        }
        ContextPlugin = nullptr;
        ContextView = nullptr;
        ContextScreen = -1;
        FlushPopup();
    }

    void Engine::CloseView(int id)
    {
        auto it = Views.find(id);
        if (it == Views.end()) return;
        auto& v = *it->second;
        for (auto& s : v.Screens)
        {
            if (!s.Alive) continue;
            s.Alive = false;
            ContextPlugin = s.Plugin;
            Guard(s.Plugin, "OnAsrContentToBeClosed", [&] { s.Object->OnAsrContentToBeClosed(); });
            s.Object = nullptr;
        }
        ContextPlugin = nullptr;
        Views.erase(it);
    }

    void Engine::RefreshView(CRadarView& v)
    {
        v.EnsureLayers();
        if (v.Back.Dc == nullptr) return;
        COLORREF key = v.KeyColor;
        HBRUSH brush = CreateSolidBrush(key);
        RECT all{0, 0, v.Width, v.Height};
        FillRect(v.Back.Dc, &all, brush);
        FillRect(v.Front.Dc, &all, brush);
        DeleteObject(brush);
        v.Building.clear();
        for (int phase = REFRESH_PHASE_BACK_BITMAP; phase <= REFRESH_PHASE_AFTER_LISTS; phase++)
        {
            HDC dc = phase < REFRESH_PHASE_AFTER_TAGS ? v.Back.Dc : v.Front.Dc;
            for (size_t i = 0; i < v.Screens.size(); i++)
            {
                auto& s = v.Screens[i];
                if (!s.Alive || s.Object == nullptr) continue;
                ContextPlugin = s.Plugin;
                ContextView = &v;
                ContextScreen = static_cast<int>(i);
                int saved = SaveDC(dc);
                Guard(s.Plugin, "OnRefresh", [&] { s.Object->OnRefresh(dc, phase); });
                RestoreDC(dc, saved);
            }
        }
        ContextPlugin = nullptr;
        ContextView = nullptr;
        ContextScreen = -1;
        GdiFlush();
        v.Objects = v.Building;
        FlushPopup();

        Writer w(Msg::ViewDrawn);
        w.I32(v.Id);
        w.Str(v.Back.Name);
        w.Str(v.Front.Name);
        w.I32(v.Width);
        w.I32(v.Height);
        w.Bool(true);
        w.I32(static_cast<int32_t>(v.Objects.size()));
        for (auto& o : v.Objects)
        {
            w.I32(o.ScreenIndex);
            w.I32(o.ObjectType);
            w.Str(ToUtf8(o.ObjectId.c_str()));
            w.R(o.Area);
            w.Bool(o.Moveable);
            w.Str(ToUtf8(o.Message.c_str()));
        }
        Send(w);
    }

    // ---- tag values and lists -------------------------------------------------------------------

    void Engine::SendTagValues(bool all)
    {
        static std::map<std::string, std::string> last;  // key → value sent
        if (all) last.clear();
        std::set<std::pair<int, int>> items = TagItemsInUse;
        std::map<std::string, int> pluginIds;
        for (auto& p : Plugins)
            if (p->Instance && !p->Unloading) pluginIds[p->Name] = p->Id;
        for (auto& list : Lists)
            for (auto& c : list->Columns)
                if (!c.ItemPlugin.empty() && pluginIds.count(c.ItemPlugin)) items.insert({pluginIds[c.ItemPlugin], c.ItemCode});
        if (items.empty()) return;

        Writer w(Msg::TagValues);
        std::vector<std::tuple<std::string, int, int, std::string, int, uint32_t, double>> changed;
        for (auto* a : AircraftOrder)
        {
            if (a->Gone) continue;
            auto fp = a->HasFlightPlan ? CPlugInData::Fp(a) : CFlightPlan();
            auto rt = a->HasRadar ? CPlugInData::Rt(a) : CRadarTarget();
            int tagData = a->HasRadar && a->HasFlightPlan ? TAG_DATA_CORRELATED : a->HasRadar ? TAG_DATA_UNCORRELATED_RADAR : TAG_DATA_FLIGHT_PLAN_TRACK;
            for (auto& item : items)
            {
                int pluginId = item.first, code = item.second;
                CPlugInData* p = nullptr;
                for (auto& q : Plugins)
                    if (q->Id == pluginId && q->Instance && !q->Unloading) p = q.get();
                if (p == nullptr) continue;
                char text[16] = {};
                int color = TAG_COLOR_DEFAULT;
                COLORREF rgb = 0;
                double size = 1.0;
                ContextPlugin = p;
                Guard(p, "OnGetTagItem", [&] { p->Instance->OnGetTagItem(fp, rt, code, tagData, text, &color, &rgb, &size); });
                text[15] = 0;
                std::string value = ToUtf8(text);
                std::string key = a->Callsign + "|" + std::to_string(pluginId) + "|" + std::to_string(code);
                std::string sig = value + "|" + std::to_string(color) + "|" + std::to_string(rgb) + "|" + std::to_string(size);
                auto it = last.find(key);
                if (it != last.end() && it->second == sig) continue;
                last[key] = sig;
                changed.emplace_back(a->Callsign, pluginId, code, value, color, static_cast<uint32_t>(rgb), size);
            }
        }
        ContextPlugin = nullptr;
        FlushPopup();
        if (changed.empty()) return;
        w.I32(static_cast<int32_t>(changed.size()));
        for (auto& [cs, pid, code, value, color, rgb, size] : changed)
        {
            w.Str(cs);
            w.I32(pid);
            w.I32(code);
            w.Str(value);
            w.I32(color);
            w.U32(rgb);
            w.F64(size);
        }
        Send(w);
    }

    void Engine::SendLists()
    {
        for (auto& list : Lists)
        {
            if (!list->Changed) continue;
            list->Changed = false;
            Writer w(Msg::FpList);
            w.I32(list->Id);
            w.I32(list->PluginId);
            w.Str(ToUtf8(list->Name.c_str()));
            w.Bool(list->Visible);
            w.I32(static_cast<int32_t>(list->Columns.size()));
            for (auto& c : list->Columns)
            {
                w.Str(ToUtf8(c.Title.c_str()));
                w.I32(c.Width);
                w.Bool(c.Centered);
                w.Str(ToUtf8(c.ItemPlugin.c_str()));
                w.I32(c.ItemCode);
                w.Str(ToUtf8(c.LeftPlugin.c_str()));
                w.I32(c.LeftFunction);
                w.Str(ToUtf8(c.RightPlugin.c_str()));
                w.I32(c.RightFunction);
            }
            w.I32(static_cast<int32_t>(list->Callsigns.size()));
            for (auto& cs : list->Callsigns) w.Str(cs);
            Send(w);
        }
    }

    // ---- settings -------------------------------------------------------------------------------

    namespace
    {
        std::string Escape(const std::string& s)
        {
            std::string r;
            for (char c : s)
            {
                if (c == '\\') r += "\\\\";
                else if (c == '\t') r += "\\t";
                else if (c == '\n') r += "\\n";
                else if (c == '\r') r += "\\r";
                else r += c;
            }
            return r;
        }

        std::string Unescape(const std::string& s)
        {
            std::string r;
            for (size_t i = 0; i < s.size(); i++)
            {
                if (s[i] != '\\' || i + 1 == s.size())
                {
                    r += s[i];
                    continue;
                }
                char n = s[++i];
                r += n == 't' ? '\t' : n == 'n' ? '\n' : n == 'r' ? '\r' : n;
            }
            return r;
        }
    }

    // One line per value: plugin, name, description, value (tab-separated, ANSI like the plugins use).
    void Engine::LoadSettings()
    {
        Settings.clear();
        std::ifstream in(SettingsFile);
        std::string line;
        while (std::getline(in, line))
        {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            std::vector<std::string> parts;
            std::string part;
            std::istringstream ls(line);
            while (std::getline(ls, part, '\t')) parts.push_back(Unescape(part));
            if (parts.size() == 4) Settings[parts[0]][parts[1]] = {parts[2], parts[3]};
        }
    }

    void Engine::SaveSettings()
    {
        if (SettingsFile.empty()) return;
        std::ofstream out(SettingsFile, std::ios::trunc);
        for (auto& [plugin, values] : Settings)
            for (auto& [name, v] : values) out << Escape(plugin) << '\t' << Escape(name) << '\t' << Escape(v.first) << '\t' << Escape(v.second) << "\n";
    }

    // ---- incoming -------------------------------------------------------------------------------

    void Engine::ReadController(Reader& r, Controller& c)
    {
        c.Callsign = r.Str();
        c.PositionId = ToAnsi(r.Str());
        c.Identified = r.Bool();
        c.Frequency = r.F64();
        c.FullName = ToAnsi(r.Str());
        c.Rating = r.I32();
        c.Facility = r.I32();
        c.SectorFile = ToAnsi(r.Str());
        c.IsController = r.Bool();
        c.Latitude = r.F64();
        c.Longitude = r.F64();
        c.Range = r.I32();
        c.Breaking = r.Bool();
        c.OngoingAble = r.Bool();
        c.Gone = false;
    }

    void Engine::ReadAircraft(Reader& r)
    {
        std::string callsign = r.Str();
        std::string key = Upper(callsign);
        auto& slot = AircraftByCallsign[key];
        bool isNew = slot == nullptr;
        if (isNew)
        {
            slot = std::make_unique<Aircraft>();
            AircraftOrder.push_back(slot.get());
        }
        Aircraft& a = *slot;
        Aircraft before = a;
        bool wasGone = a.Gone;
        a.Gone = false;
        a.Callsign = callsign;
        a.PilotName = ToAnsi(r.Str());
        a.SystemId = r.Str();
        a.HasRadar = r.Bool();
        a.HasFlightPlan = r.Bool();

        // Radar
        RadarPosition pos;
        pos.Valid = a.HasRadar;
        pos.Latitude = r.F64();
        pos.Longitude = r.F64();
        pos.PressureAltitude = r.I32();
        pos.FlightLevel = r.I32();
        pos.GroundSpeed = r.I32();
        pos.Heading = r.I32();
        pos.Squawk = r.Str();
        pos.ModeC = r.Bool();
        pos.Ident = r.Bool();
        a.VerticalSpeed = r.I32();
        a.TrackHeading = r.F64();
        pos.ReceivedTime = r.I32();
        pos.RadarFlags = r.I32();
        bool moved = pos.ReceivedTime != a.Current().ReceivedTime || pos.Latitude != a.Current().Latitude ||
                     pos.Longitude != a.Current().Longitude || !a.Current().Valid;
        if (a.HasRadar && moved)
        {
            a.Newest = (a.Newest + Aircraft::HistorySize - 1) % Aircraft::HistorySize;
            a.History[a.Newest] = pos;
        }
        a.FpTrack = pos;
        a.FpTrack.Valid = true;

        // Flight plan
        a.FpReceived = r.Bool();
        a.Amended = r.Bool();
        a.PlanType = r.Str();
        a.AircraftInfo = r.Str();
        a.AircraftFpType = r.Str();
        a.Manufacturer = ToAnsi(r.Str());
        a.Wtc = static_cast<char>(r.U8());
        a.AircraftType = static_cast<char>(r.U8());
        a.Engines = r.I32();
        a.EngineType = static_cast<char>(r.U8());
        a.Capabilities = static_cast<char>(r.U8());
        a.Rvsm = r.Bool();
        a.TrueAirspeed = r.I32();
        a.Origin = r.Str();
        a.FinalAltitude = r.I32();
        a.Destination = r.Str();
        a.Alternate = r.Str();
        a.Remarks = ToAnsi(r.Str());
        a.CommunicationType = static_cast<char>(r.U8());
        a.Route = r.Str();
        a.Sid = r.Str();
        a.Star = r.Str();
        a.DepartureRwy = r.Str();
        a.ArrivalRwy = r.Str();
        a.EstimatedDeparture = r.Str();
        a.ActualDeparture = r.Str();
        a.EnrouteHours = r.Str();
        a.EnrouteMinutes = r.Str();
        a.FuelHours = r.Str();
        a.FuelMinutes = r.Str();

        // Controller assigned data
        a.AssignedSquawk = r.Str();
        a.AssignedFinalAltitude = r.I32();
        a.ClearedAltitude = r.I32();
        a.AssignedCommunicationType = static_cast<char>(r.U8());
        a.ScratchPad = ToAnsi(r.Str());
        a.AssignedSpeed = r.I32();
        a.AssignedMach = r.I32();
        a.AssignedRate = r.I32();
        a.AssignedHeading = r.I32();
        a.DirectTo = r.Str();
        int annotations = r.I32();
        for (int i = 0; i < annotations && r.Ok(); i++)
        {
            std::string s = ToAnsi(r.Str());
            if (i < 9) a.Annotations[static_cast<size_t>(i)] = s;
        }

        // States
        a.State = r.I32();
        a.FpState = r.I32();
        a.Simulated = r.Bool();
        a.TrackingCallsign = r.Str();
        a.TrackingId = ToAnsi(r.Str());
        a.TrackingIsMe = r.Bool();
        a.HandoffCallsign = r.Str();
        a.HandoffId = ToAnsi(r.Str());
        a.DistanceToDestination = r.F64();
        a.DistanceFromOrigin = r.F64();
        a.NextCopx = r.Str();
        a.NextFirCopx = r.Str();
        a.SectorEntryMinutes = r.I32();
        a.SectorExitMinutes = r.I32();
        a.Ram = r.Bool();
        a.Clam = r.Bool();
        a.GroundState = r.Str();
        a.Clearance = r.Bool();
        a.TextCommunication = r.Bool();
        a.CoordinatedNextController = r.Str();
        a.CoordinatedNextControllerState = r.I32();
        a.EntryPointState = r.I32();
        a.EntryPoint = r.Str();
        a.EntryAltitudeState = r.I32();
        a.EntryAltitude = r.I32();
        a.ExitPointState = r.I32();
        a.ExitPoint = r.Str();
        a.ExitAltitudeState = r.I32();
        a.ExitAltitude = r.I32();

        // Route and predictions
        a.RouteCalculatedIndex = r.I32();
        a.RouteAssignedIndex = r.I32();
        int points = r.I32();
        a.Route_.clear();
        for (int i = 0; i < points && r.Ok(); i++)
        {
            RoutePoint p;
            p.Name = r.Str();
            p.Latitude = r.F64();
            p.Longitude = r.F64();
            p.Airway = r.Str();
            p.AirwayClass = r.I32();
            p.Minutes = r.I32();
            p.ProfileAltitude = r.I32();
            a.Route_.push_back(p);
        }
        int predictions = r.I32();
        a.Predictions.clear();
        for (int i = 0; i < predictions && r.Ok(); i++)
        {
            Prediction p;
            p.Latitude = r.F64();
            p.Longitude = r.F64();
            p.Altitude = r.I32();
            p.ControllerId = ToAnsi(r.Str());
            a.Predictions.push_back(p);
        }

        // Events, like EuroScope raises them.
        bool fresh = isNew || wasGone;
        if (a.HasRadar && moved) FireTargetUpdate(&a);
        if (a.HasFlightPlan)
        {
            bool fpChanged = fresh || before.Route != a.Route || before.Origin != a.Origin || before.Destination != a.Destination ||
                             before.FinalAltitude != a.FinalAltitude || before.AircraftInfo != a.AircraftInfo || before.Remarks != a.Remarks ||
                             before.Sid != a.Sid || before.Star != a.Star || before.DepartureRwy != a.DepartureRwy ||
                             before.ArrivalRwy != a.ArrivalRwy || before.TrueAirspeed != a.TrueAirspeed || before.PlanType != a.PlanType ||
                             before.Alternate != a.Alternate || before.EstimatedDeparture != a.EstimatedDeparture;
            if (fpChanged) FireFlightPlanDataUpdate(&a);
            if (fresh || before.AssignedSquawk != a.AssignedSquawk) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_SQUAWK);
            if (fresh || before.AssignedFinalAltitude != a.AssignedFinalAltitude) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_FINAL_ALTITUDE);
            if (fresh || before.ClearedAltitude != a.ClearedAltitude) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_TEMPORARY_ALTITUDE);
            if (before.AssignedCommunicationType != a.AssignedCommunicationType) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_COMMUNICATION_TYPE);
            if (before.ScratchPad != a.ScratchPad) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_SCRATCH_PAD_STRING);
            if (before.GroundState != a.GroundState) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_GROUND_STATE);
            if (before.Clearance != a.Clearance) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_CLEARENCE_FLAG);
            if (before.AssignedSpeed != a.AssignedSpeed) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_SPEED);
            if (before.AssignedMach != a.AssignedMach) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_MACH);
            if (before.AssignedRate != a.AssignedRate) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_RATE);
            if (before.AssignedHeading != a.AssignedHeading) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_HEADING);
            if (before.DirectTo != a.DirectTo) FireAssignedDataUpdate(&a, CTR_DATA_TYPE_DIRECT_TO);
        }
    }

    void Engine::Handle(const std::vector<uint8_t>& frame)
    {
        if (frame.empty()) return;
        Reader r(frame.data() + 1, frame.size() - 1);
        switch (static_cast<Msg>(frame[0]))
        {
        case Msg::Hello:
            SettingsFile = ToAnsi(r.Str());
            TransitionAltitude = r.I32();
            LoadSettings();
            break;
        case Msg::LoadPlugin:
            LoadPlugin(r.Str());
            break;
        case Msg::UnloadPlugin:
        {
            int id = r.I32();
            for (auto& p : Plugins)
                if (p->Id == id) UnloadPlugin(p.get());
            break;
        }
        case Msg::Myself:
            ReadController(r, Myself);
            ConnectionType = r.I32();
            break;
        case Msg::Controller:
        {
            Controller c;
            ReadController(r, c);
            auto& slot = ControllersByCallsign[Upper(c.Callsign)];
            if (slot == nullptr)
            {
                slot = std::make_unique<Controller>();
                ControllerOrder.push_back(slot.get());
            }
            *slot = c;
            auto handle = CPlugInData::Ctr(slot.get());
            EachPlugin([&](CPlugInData& p) { Guard(&p, "OnControllerPositionUpdate", [&] { p.Instance->OnControllerPositionUpdate(handle); }); });
            EachScreen([&](CRadarView&, int, CRadarScreen& s) { Guard(ContextPlugin, "OnControllerPositionUpdate", [&] { s.OnControllerPositionUpdate(handle); }); });
            break;
        }
        case Msg::ControllerGone:
        {
            if (auto* c = FindController(r.Str().c_str()))
            {
                auto handle = CPlugInData::Ctr(c);
                EachPlugin([&](CPlugInData& p) { Guard(&p, "OnControllerDisconnect", [&] { p.Instance->OnControllerDisconnect(handle); }); });
                EachScreen([&](CRadarView&, int, CRadarScreen& s) { Guard(ContextPlugin, "OnControllerDisconnect", [&] { s.OnControllerDisconnect(handle); }); });
                c->Gone = true;
            }
            break;
        }
        case Msg::Aircraft:
            ReadAircraft(r);
            break;
        case Msg::AircraftGone:
        {
            if (auto* a = FindAircraft(r.Str().c_str()))
            {
                auto fp = CPlugInData::Fp(a);
                EachPlugin([&](CPlugInData& p) { Guard(&p, "OnFlightPlanDisconnect", [&] { p.Instance->OnFlightPlanDisconnect(fp); }); });
                EachScreen([&](CRadarView&, int, CRadarScreen& s) { Guard(ContextPlugin, "OnFlightPlanDisconnect", [&] { s.OnFlightPlanDisconnect(fp); }); });
                a->Gone = true;
                for (auto& list : Lists)
                {
                    auto& cs = list->Callsigns;
                    auto it = std::find(cs.begin(), cs.end(), a->Callsign);
                    if (it != cs.end())
                    {
                        cs.erase(it);
                        list->Changed = true;
                    }
                }
            }
            break;
        }
        case Msg::SectorReset:
            SectorFileName = ToAnsi(r.Str());
            for (auto& e : Elements) e.clear();
            break;
        case Msg::SectorElement:
        {
            SectorElement e;
            e.Type = r.I32();
            e.Name = ToAnsi(r.Str());
            e.Airport = r.Str();
            e.Frequency = r.F64();
            int n = r.I32();
            for (int i = 0; i < n && r.Ok(); i++)
            {
                double lat = r.F64();
                double lon = r.F64();
                e.Positions.emplace_back(lat, lon);
            }
            n = r.I32();
            for (int i = 0; i < n && r.Ok(); i++) e.Components.push_back(ToAnsi(r.Str()));
            e.RunwayNames[0] = r.Str();
            e.RunwayNames[1] = r.Str();
            e.RunwayHeadings[0] = r.I32();
            e.RunwayHeadings[1] = r.I32();
            e.Departure[0] = r.Bool();
            e.Arrival[0] = r.Bool();
            e.Departure[1] = r.Bool();
            e.Arrival[1] = r.Bool();
            if (r.Ok() && e.Type >= 0 && e.Type < static_cast<int>(Elements.size())) Elements[static_cast<size_t>(e.Type)].push_back(e);
            break;
        }
        case Msg::SectorDone:
            EachPlugin([&](CPlugInData& p) { Guard(&p, "OnAirportRunwayActivityChanged", [&] { p.Instance->OnAirportRunwayActivityChanged(); }); });
            break;
        case Msg::Asel:
            Asel = r.Str();
            break;
        case Msg::Command:
        {
            int requestId = r.I32();
            std::string text = ToAnsi(r.Str());
            bool handled = false;
            EachPlugin([&](CPlugInData& p) {
                if (!handled) Guard(&p, "OnCompileCommand", [&] { handled = p.Instance->OnCompileCommand(text.c_str()); });
            });
            if (!handled)
                EachScreen([&](CRadarView&, int, CRadarScreen& s) {
                    if (!handled) Guard(ContextPlugin, "OnCompileCommand", [&] { handled = s.OnCompileCommand(text.c_str()); });
                });
            FlushPopup();
            Writer w(Msg::CommandResult);
            w.I32(requestId);
            w.Bool(handled);
            Send(w);
            break;
        }
        case Msg::Chat:
        {
            bool isPrivate = r.Bool();
            std::string sender = r.Str(), receiver = r.Str();
            double freq = r.F64();
            std::string text = ToAnsi(r.Str());
            EachPlugin([&](CPlugInData& p) {
                if (isPrivate)
                    Guard(&p, "OnCompilePrivateChat", [&] { p.Instance->OnCompilePrivateChat(sender.c_str(), receiver.c_str(), text.c_str()); });
                else
                    Guard(&p, "OnCompileFrequencyChat", [&] { p.Instance->OnCompileFrequencyChat(sender.c_str(), freq, text.c_str()); });
            });
            FlushPopup();
            break;
        }
        case Msg::Metar:
        {
            std::string station = r.Str(), metar = r.Str();
            EachPlugin([&](CPlugInData& p) { Guard(&p, "OnNewMetarReceived", [&] { p.Instance->OnNewMetarReceived(station.c_str(), metar.c_str()); }); });
            break;
        }
        case Msg::TagItemsInUse:
        {
            TagItemsInUse.clear();
            int n = r.I32();
            for (int i = 0; i < n && r.Ok(); i++)
            {
                int pid = r.I32();
                int code = r.I32();
                TagItemsInUse.insert({pid, code});
            }
            SendTagValues(true);
            break;
        }
        case Msg::FunctionCall:
        {
            int pluginId = r.I32(), fid = r.I32();
            std::string callsign = r.Str();
            std::string item = ToAnsi(r.Str());
            POINT pt{r.I32(), r.I32()};
            Rect a = r.R();
            RECT area{a.left, a.top, a.right, a.bottom};
            if (!callsign.empty()) Asel = callsign;
            for (auto& p : Plugins)
            {
                if (p->Id != pluginId || p->Instance == nullptr || p->Unloading) continue;
                ContextPlugin = p.get();
                Guard(p.get(), "OnFunctionCall", [&] { p->Instance->OnFunctionCall(fid, item.c_str(), pt, area); });
                ContextPlugin = nullptr;
            }
            FlushPopup();
            SendTagValues(false);
            break;
        }
        case Msg::PopupSelect:
        {
            int popupId = r.I32(), fid = r.I32();
            std::string text = ToAnsi(r.Str());
            POINT pt{r.I32(), r.I32()};
            Rect a = r.R();
            RECT area{a.left, a.top, a.right, a.bottom};
            auto it = Popups.find(popupId);
            if (it == Popups.end()) break;
            PopupTarget t = it->second;
            if (t.Plugin == nullptr || t.Plugin->Instance == nullptr || t.Plugin->Unloading) break;
            ContextPlugin = t.Plugin;
            auto view = Views.find(t.ViewId);
            if (t.ScreenIndex >= 0 && view != Views.end() && t.ScreenIndex < static_cast<int>(view->second->Screens.size()) &&
                view->second->Screens[static_cast<size_t>(t.ScreenIndex)].Alive)
            {
                ContextView = view->second.get();
                ContextScreen = t.ScreenIndex;
                auto* s = view->second->Screens[static_cast<size_t>(t.ScreenIndex)].Object;
                Guard(t.Plugin, "OnFunctionCall", [&] { s->OnFunctionCall(fid, text.c_str(), pt, area); });
            }
            else
                Guard(t.Plugin, "OnFunctionCall", [&] { t.Plugin->Instance->OnFunctionCall(fid, text.c_str(), pt, area); });
            ContextPlugin = nullptr;
            ContextView = nullptr;
            ContextScreen = -1;
            FlushPopup();
            SendTagValues(false);
            break;
        }
        case Msg::ViewOpen:
        {
            int id = r.I32();
            std::string type = r.Str();
            int n = r.I32();
            std::vector<std::pair<std::string, std::string>> data;
            for (int i = 0; i < n && r.Ok(); i++)
            {
                std::string name = r.Str();
                std::string value = r.Str();
                data.emplace_back(name, value);
            }
            OpenView(id, type, data);
            break;
        }
        case Msg::ViewClose:
            CloseView(r.I32());
            break;
        case Msg::ViewGeometry:
        {
            int id = r.I32();
            auto it = Views.find(id);
            if (it == Views.end()) break;
            auto& v = *it->second;
            v.Width = r.I32();
            v.Height = r.I32();
            v.ProjectionLatitude = r.F64();
            v.ProjectionLongitude = r.F64();
            v.CenterX = r.F64();
            v.CenterY = r.F64();
            v.NmPerPixel = r.F64();
            v.KeyColor = r.U32();
            v.RadarArea = r.R();
            v.ToolbarArea = r.R();
            v.ChatArea = r.R();
            break;
        }
        case Msg::ViewRefresh:
        {
            auto it = Views.find(r.I32());
            if (it != Views.end()) RefreshView(*it->second);
            break;
        }
        case Msg::ScreenObjectEvent:
        {
            int viewId = r.I32(), screenIndex = r.I32(), kind = r.I32(), type = r.I32();
            std::string id = ToAnsi(r.Str());
            POINT pt{r.I32(), r.I32()};
            Rect a = r.R();
            RECT area{a.left, a.top, a.right, a.bottom};
            int extra = r.I32();
            auto it = Views.find(viewId);
            if (it == Views.end() || screenIndex < 0 || screenIndex >= static_cast<int>(it->second->Screens.size())) break;
            auto& s = it->second->Screens[static_cast<size_t>(screenIndex)];
            if (!s.Alive) break;
            ContextPlugin = s.Plugin;
            ContextView = it->second.get();
            ContextScreen = screenIndex;
            auto* o = s.Object;
            Guard(s.Plugin, "screen object event", [&] {
                switch (kind)
                {
                case 0: o->OnOverScreenObject(type, id.c_str(), pt, area); break;
                case 1: o->OnButtonDownScreenObject(type, id.c_str(), pt, area, extra); break;
                case 2: o->OnButtonUpScreenObject(type, id.c_str(), pt, area, extra); break;
                case 3: o->OnClickScreenObject(type, id.c_str(), pt, area, extra); break;
                case 4: o->OnDoubleClickScreenObject(type, id.c_str(), pt, area, extra); break;
                case 5: o->OnMoveScreenObject(type, id.c_str(), pt, area, extra != 0); break;
                default: break;
                }
            });
            ContextPlugin = nullptr;
            ContextView = nullptr;
            ContextScreen = -1;
            FlushPopup();
            break;
        }
        case Msg::ViewSave:
        {
            auto it = Views.find(r.I32());
            if (it == Views.end()) break;
            for (size_t i = 0; i < it->second->Screens.size(); i++)
            {
                auto& s = it->second->Screens[i];
                if (!s.Alive) continue;
                ContextPlugin = s.Plugin;
                ContextView = it->second.get();
                ContextScreen = static_cast<int>(i);
                Guard(s.Plugin, "OnAsrContentToBeSaved", [&] { s.Object->OnAsrContentToBeSaved(); });
            }
            ContextPlugin = nullptr;
            ContextView = nullptr;
            ContextScreen = -1;
            break;
        }
        case Msg::PlaneInfo:
        {
            std::string cs = r.Str(), livery = r.Str(), type = r.Str();
            EachPlugin([&](CPlugInData& p) {
                Guard(&p, "OnPlaneInformationUpdate", [&] { p.Instance->OnPlaneInformationUpdate(cs.c_str(), livery.c_str(), type.c_str()); });
            });
            break;
        }
        case Msg::Channels:
        {
            Channels.clear();
            int n = r.I32();
            for (int i = 0; i < n && r.Ok(); i++)
            {
                Channel c;
                c.Name = r.Str();
                c.Frequency = r.F64();
                c.Primary = r.Bool();
                c.Atis = r.Bool();
                c.TextReceive = r.Bool();
                c.TextTransmit = r.Bool();
                c.VoiceReceive = r.Bool();
                c.VoiceTransmit = r.Bool();
                c.VoiceConnected = r.Bool();
                Channels.push_back(c);
            }
            break;
        }
        case Msg::VoiceEvent:
        {
            int kind = r.I32();
            bool primary = r.Bool();
            int channel = r.I32();
            auto handle = CPlugInData::ChannelHandle(channel);
            EachPlugin([&](CPlugInData& p) {
                Guard(&p, "voice event", [&] {
                    if (kind == 0) p.Instance->OnVoiceTransmitStarted(primary);
                    else if (kind == 1) p.Instance->OnVoiceTransmitEnded(primary);
                    else if (kind == 2) p.Instance->OnVoiceReceiveStarted(handle);
                    else p.Instance->OnVoiceReceiveEnded(handle);
                });
            });
            break;
        }
        case Msg::FpListAction:
            OnTimer();
            break;
        case Msg::Quit:
            PostQuitMessage(0);
            break;
        default:
            break;
        }
    }
}

// ---- CRadarView -------------------------------------------------------------------------------------

CRadarView::~CRadarView() { FreeLayers(); }

POINT CRadarView::ToPixel(double latitude, double longitude) const
{
    using namespace natc;
    // Azimuthal equidistant projection, the same as the radar of Network-ATC (Geo/Projection.cs).
    double lat0 = ProjectionLatitude * Rad, lat = latitude * Rad, dLon = (longitude - ProjectionLongitude) * Rad;
    double cosC = std::sin(lat0) * std::sin(lat) + std::cos(lat0) * std::cos(lat) * std::cos(dLon);
    cosC = std::max(-1.0, std::min(1.0, cosC));
    double c = std::acos(cosC);
    double k = c < 1e-12 ? 1 : c / std::sin(c);
    double x = k * std::cos(lat) * std::sin(dLon) * EarthRadiusNm;
    double y = k * (std::cos(lat0) * std::sin(lat) - std::sin(lat0) * std::cos(lat) * std::cos(dLon)) * EarthRadiusNm;
    POINT p;
    p.x = static_cast<LONG>(std::lround((x - CenterX) / NmPerPixel + Width / 2.0));
    p.y = static_cast<LONG>(std::lround(Height / 2.0 - (y - CenterY) / NmPerPixel));
    return p;
}

void CRadarView::ToPosition(POINT pt, double& latitude, double& longitude) const
{
    using namespace natc;
    double xNm = (pt.x - Width / 2.0) * NmPerPixel + CenterX, yNm = (Height / 2.0 - pt.y) * NmPerPixel + CenterY;
    double x = xNm / EarthRadiusNm, y = yNm / EarthRadiusNm;
    double lat0 = ProjectionLatitude * Rad;
    double c = std::sqrt(x * x + y * y);
    if (c < 1e-12)
    {
        latitude = ProjectionLatitude;
        longitude = ProjectionLongitude;
        return;
    }
    double lat = std::asin(std::cos(c) * std::sin(lat0) + y * std::sin(c) * std::cos(lat0) / c);
    double lon = ProjectionLongitude * Rad + std::atan2(x * std::sin(c), c * std::cos(lat0) * std::cos(c) - y * std::sin(lat0) * std::sin(c));
    latitude = lat / Rad;
    longitude = std::fmod(lon / Rad + 540.0, 360.0) - 180.0;
}

void CRadarView::EnsureLayers()
{
    if (Width <= 0 || Height <= 0) return;
    if (Back.Dc != nullptr)
    {
        BITMAP bm{};
        GetObjectA(Back.Bitmap, sizeof bm, &bm);
        if (bm.bmWidth == Width && bm.bmHeight == Height) return;
    }
    FreeLayers();
    Generation++;
    auto make = [&](Layer& l, const char* which) {
        char name[128];
        snprintf(name, sizeof name, "Local\\NatcEs_%lu_%d_%d_%s", GetCurrentProcessId(), Id, Generation, which);
        l.Name = name;
        DWORD bytes = static_cast<DWORD>(Width) * static_cast<DWORD>(Height) * 4;
        l.Mapping = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bytes, name);
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = Width;
        bi.bmiHeader.biHeight = -Height;  // top-down
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        l.Bitmap = CreateDIBSection(nullptr, &bi, DIB_RGB_COLORS, &l.Bits, l.Mapping, 0);
        l.Dc = CreateCompatibleDC(nullptr);
        SelectObject(l.Dc, l.Bitmap);
    };
    make(Back, "back");
    make(Front, "front");
}

void CRadarView::FreeLayers()
{
    for (Layer* l : {&Back, &Front})
    {
        if (l->Dc) DeleteDC(l->Dc);
        if (l->Bitmap) DeleteObject(l->Bitmap);
        if (l->Mapping) CloseHandle(l->Mapping);
        *l = Layer{};
    }
}
