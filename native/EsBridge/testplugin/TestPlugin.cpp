// A small plugin written like the EuroScope plugins in the wild: it uses tag items and functions, popups,
// a radar screen with a screen object, commands, settings, a flight plan list and the data classes.
// The host tests (tests/NetworkAtc.EsHost.Tests) drive it through Network-ATC's side of the pipe.
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>

#include "../include/EsPlugInApi.h"

using namespace EuroScopePlugIn;

namespace
{
    constexpr int ItemCflPlus = 1;       // CFL / 100 with a plus sign: "+350"
    constexpr int ItemRouteInfo = 2;     // number of route points and the first SID letter
    constexpr int FunctionMenu = 10;     // opens a popup list
    constexpr int FunctionSetCfl = 11;   // popup element: set CFL from the element string
    constexpr int FunctionEditScratch = 12;
    constexpr int FunctionScratchDone = 13;

    class TestScreen final : public CRadarScreen
    {
    public:
        int Refreshes = 0;
        std::string LastClick;

        void OnAsrContentToBeClosed() override { delete this; }

        void OnAsrContentLoaded(bool Loaded) override
        {
            const char* v = GetDataFromAsr("TestValue");
            if (Loaded && v != nullptr) LastClick = v;
        }

        void OnRefresh(HDC hDC, int Phase) override
        {
            if (Phase != REFRESH_PHASE_AFTER_TAGS) return;
            Refreshes++;
            // A red box around every aircraft, clickable.
            for (CRadarTarget rt = GetPlugIn()->RadarTargetSelectFirst(); rt.IsValid(); rt = GetPlugIn()->RadarTargetSelectNext(rt))
            {
                POINT p = ConvertCoordFromPositionToPixel(rt.GetPosition().GetPosition());
                RECT r{p.x - 6, p.y - 6, p.x + 6, p.y + 6};
                HBRUSH brush = CreateSolidBrush(RGB(255, 0, 0));
                FrameRect(hDC, &r, brush);
                DeleteObject(brush);
                AddScreenObject(7, rt.GetCallsign(), r, false, "test box");
            }
        }

        void OnClickScreenObject(int ObjectType, const char* sObjectId, POINT Pt, RECT Area, int Button) override
        {
            if (ObjectType != 7) return;
            LastClick = sObjectId;
            SaveDataToAsr("TestValue", "Last clicked", sObjectId);
            GetPlugIn()->OpenPopupList(Area, "SCREEN", 1);
            GetPlugIn()->AddPopupListElement("A", "", 99);
            (void)Pt;
            (void)Button;
        }

        void OnFunctionCall(int FunctionId, const char* sItemString, POINT Pt, RECT Area) override
        {
            (void)Pt;
            (void)Area;
            if (FunctionId == 99)
                GetPlugIn()->DisplayUserMessage("TEST", "screen", sItemString, true, true, false, false, false);
        }
    };

    class TestPlugin : public CPlugIn
    {
    public:
        CFlightPlanList List;
        int Timer = 0;

        TestPlugin() : CPlugIn(COMPATIBILITY_CODE, "Test Plugin", "1.0", "SkyNetwork", "Free")
        {
            RegisterTagItemType("CFL plus", ItemCflPlus);
            RegisterTagItemType("Route info", ItemRouteInfo);
            RegisterTagItemFunction("Test menu", FunctionMenu);
            RegisterTagItemFunction("Edit scratch", FunctionEditScratch);
            RegisterDisplayType("Test display", false, true, true, true);
            List = RegisterFpList("Test list");
            List.AddColumnDefinition("C/S", 8, false, nullptr, TAG_ITEM_TYPE_CALLSIGN, nullptr, TAG_ITEM_FUNCTION_NO, nullptr, TAG_ITEM_FUNCTION_NO);
            List.AddColumnDefinition("CFL", 5, true, "Test Plugin", ItemCflPlus, "Test Plugin", FunctionMenu, nullptr, TAG_ITEM_FUNCTION_NO);
            if (GetDataFromSettings("Greeting") == nullptr) SaveDataToSettings("Greeting", "A greeting", "hello");
        }

        CRadarScreen* OnRadarScreenCreated(const char* sDisplayName, bool, bool, bool, bool) override
        {
            return strcmp(sDisplayName, "Test display") == 0 || strcmp(sDisplayName, "Standard ES radar screen") == 0 ? new TestScreen() : nullptr;
        }

        void OnGetTagItem(CFlightPlan FlightPlan, CRadarTarget RadarTarget, int ItemCode, int TagData, char sItemString[16], int* pColorCode,
                          COLORREF* pRGB, double* pFontSize) override
        {
            (void)TagData;
            (void)pFontSize;
            if (ItemCode == ItemCflPlus && FlightPlan.IsValid())
            {
                snprintf(sItemString, 16, "+%03d", FlightPlan.GetControllerAssignedData().GetClearedAltitude() / 100);
                *pColorCode = TAG_COLOR_RGB_DEFINED;
                *pRGB = RGB(0, 200, 0);
            }
            if (ItemCode == ItemRouteInfo && FlightPlan.IsValid())
            {
                CFlightPlanExtractedRoute route = FlightPlan.GetExtractedRoute();
                snprintf(sItemString, 16, "%d%s", route.GetPointsNumber(), RadarTarget.IsValid() ? "R" : "-");
            }
        }

        void OnFunctionCall(int FunctionId, const char* sItemString, POINT Pt, RECT Area) override
        {
            (void)Pt;
            CFlightPlan fp = FlightPlanSelectASEL();
            if (FunctionId == FunctionMenu)
            {
                OpenPopupList(Area, "CFL", 1);
                AddPopupListElement("100", "", FunctionSetCfl);
                AddPopupListElement("200", "", FunctionSetCfl, true);
            }
            else if (FunctionId == FunctionSetCfl && fp.IsValid())
            {
                fp.GetControllerAssignedData().SetClearedAltitude(atoi(sItemString) * 100);
            }
            else if (FunctionId == FunctionEditScratch && fp.IsValid())
            {
                OpenPopupEdit(Area, FunctionScratchDone, fp.GetControllerAssignedData().GetScratchPadString());
            }
            else if (FunctionId == FunctionScratchDone && fp.IsValid())
            {
                fp.GetControllerAssignedData().SetScratchPadString(sItemString);
            }
        }

        bool OnCompileCommand(const char* sCommandLine) override
        {
            if (_strnicmp(sCommandLine, ".test", 5) != 0) return false;
            CController me = ControllerMyself();
            char text[256];
            snprintf(text, sizeof text, "me=%s freq=%.3f ta=%d greeting=%s", me.GetCallsign(), me.GetPrimaryFrequency(), GetTransitionAltitude(),
                     GetDataFromSettings("Greeting"));
            DisplayUserMessage("TEST", "plugin", text, true, true, false, false, false);
            CFlightPlan fp = FlightPlanSelect("AFL123");
            if (fp.IsValid() && strstr(sCommandLine, "assume")) fp.StartTracking();
            if (fp.IsValid() && strstr(sCommandLine, "handoff")) fp.InitiateHandoff("UUWV_CTR");
            return true;
        }

        void OnRefreshFpListContent(CFlightPlanList AcList) override
        {
            for (CFlightPlan fp = FlightPlanSelectFirst(); fp.IsValid(); fp = FlightPlanSelectNext(fp)) AcList.AddFpToTheList(fp);
            AcList.ShowFpList(true);
        }

        void OnTimer(int Counter) override { Timer = Counter; }
    };

    TestPlugin* g_Plugin = nullptr;
}

void __declspec(dllexport) EuroScopePlugInInit(EuroScopePlugIn::CPlugIn** ppPlugInInstance)
{
    *ppPlugInInstance = g_Plugin = new TestPlugin();
}

void __declspec(dllexport) EuroScopePlugInExit(void)
{
    delete g_Plugin;
    g_Plugin = nullptr;
}
