// The plugin side of the interface: CPlugIn, radar screens, controllers, sector elements, lists and channels.
#include <algorithm>
#include <cmath>

#include "Engine.h"

using namespace EuroScopePlugIn;
using natc::Engine;

namespace
{
    const char* const Empty = "";
    std::string Str(const char* s) { return s ? std::string(s) : std::string(); }
    natc::Rect ToRect(const RECT& r) { return {r.left, r.top, r.right, r.bottom}; }
}

// ---- handles ----------------------------------------------------------------------------------------------

CFlightPlan CPlugInData::Fp(natc::Aircraft* a)
{
    CFlightPlan fp;
    fp.m_FpPosition = a;
    return fp;
}

CRadarTarget CPlugInData::Rt(natc::Aircraft* a)
{
    CRadarTarget rt;
    rt.m_RtPosition = a;
    return rt;
}

CController CPlugInData::Ctr(natc::Controller* c, bool myself)
{
    CController ctr;
    ctr.m_CtrPosition = c;
    ctr.m_Myself = myself;
    return ctr;
}

CSectorElement CPlugInData::Element(int index, int type)
{
    CSectorElement e;
    e.m_Position = index;
    e.m_ElementType = type;
    return e;
}

CGrountToAirChannel CPlugInData::ChannelHandle(int index)
{
    CGrountToAirChannel c;
    c.m_Index = index;
    return c;
}

CFlightPlanList CPlugInData::List(natc::FpList* list)
{
    CFlightPlanList l;
    l.m_Position = list;
    return l;
}

// ---- CController ----------------------------------------------------------------------------------------

namespace
{
    const natc::Controller* Ctl(void* p, bool myself)
    {
        if (myself) return &Engine::Get().Myself;
        return static_cast<natc::Controller*>(p);
    }
}

#define CTL Ctl(m_CtrPosition, m_Myself)

const char* CController::GetCallsign(void) const { return CTL ? CTL->Callsign.c_str() : Empty; }
const char* CController::GetPositionId(void) const { return CTL ? CTL->PositionId.c_str() : Empty; }
bool CController::GetPositionIdentified(void) const { return CTL && CTL->Identified; }
double CController::GetPrimaryFrequency(void) const { return CTL ? CTL->Frequency : 199.998; }
const char* CController::GetFullName(void) const { return CTL ? CTL->FullName.c_str() : Empty; }
int CController::GetRating(void) const { return CTL ? CTL->Rating : 0; }
int CController::GetFacility(void) const { return CTL ? CTL->Facility : 0; }
const char* CController::GetSectorFileName(void) const { return CTL ? CTL->SectorFile.c_str() : Empty; }
bool CController::IsController(void) const { return CTL && CTL->IsController; }

CPosition CController::GetPosition(void) const
{
    CPosition p;
    if (CTL)
    {
        p.m_Latitude = CTL->Latitude;
        p.m_Longitude = CTL->Longitude;
    }
    return p;
}

int CController::GetRange(void) const { return CTL ? CTL->Range : 0; }
bool CController::IsBreaking(void) const { return CTL && CTL->Breaking; }
bool CController::IsOngoingAble(void) const { return CTL && CTL->OngoingAble; }

// ---- CRadarScreen ---------------------------------------------------------------------------------------

CRadarScreen::CRadarScreen(void)
{
    m_pRadarView = nullptr;
    m_pPlugIn = nullptr;
}

namespace
{
    RECT ToWin(const natc::Rect& r) { return RECT{r.left, r.top, r.right, r.bottom}; }

    /// The index of this screen object in its view, for screen objects and popups.
    int IndexOf(CRadarView* v, CRadarScreen* s)
    {
        if (v == nullptr) return -1;
        for (size_t i = 0; i < v->Screens.size(); i++)
            if (v->Screens[i].Object == s) return static_cast<int>(i);
        return -1;
    }
}

RECT CRadarScreen::GetToolbarArea(void) { return m_pRadarView ? ToWin(m_pRadarView->ToolbarArea) : RECT{0, 0, 0, 0}; }
RECT CRadarScreen::GetRadarArea(void) { return m_pRadarView ? ToWin(m_pRadarView->RadarArea) : RECT{0, 0, 0, 0}; }
RECT CRadarScreen::GetChatArea(void) { return m_pRadarView ? ToWin(m_pRadarView->ChatArea) : RECT{0, 0, 0, 0}; }

CPosition CRadarScreen::ConvertCoordFromPixelToPosition(POINT Pt)
{
    CPosition p;
    if (m_pRadarView) m_pRadarView->ToPosition(Pt, p.m_Latitude, p.m_Longitude);
    return p;
}

POINT CRadarScreen::ConvertCoordFromPositionToPixel(CPosition Pos)
{
    if (!m_pRadarView) return POINT{0, 0};
    return m_pRadarView->ToPixel(Pos.m_Latitude, Pos.m_Longitude);
}

void CRadarScreen::SaveDataToAsr(const char* sVariableName, const char* sVariableDescription, const char* sValue)
{
    if (!m_pRadarView || sVariableName == nullptr) return;
    m_pRadarView->Asr[sVariableName] = {Str(sVariableDescription), Str(sValue)};
    natc::Writer w(natc::Msg::ViewData);
    w.I32(m_pRadarView->Id);
    w.Str(natc::ToUtf8(sVariableName));
    w.Str(natc::ToUtf8(sVariableDescription));
    w.Str(natc::ToUtf8(sValue));
    Engine::Get().Send(w);
}

const char* CRadarScreen::GetDataFromAsr(const char* sVariableName)
{
    if (!m_pRadarView || sVariableName == nullptr) return nullptr;
    auto it = m_pRadarView->Asr.find(sVariableName);
    return it == m_pRadarView->Asr.end() ? nullptr : it->second.second.c_str();
}

void CRadarScreen::AddScreenObject(int ObjectType, const char* sObjectId, RECT Area, bool Moveable, const char* sMessage)
{
    if (!m_pRadarView) return;
    natc::ScreenObject o;
    o.ScreenIndex = IndexOf(m_pRadarView, this);
    o.ObjectType = ObjectType;
    o.ObjectId = Str(sObjectId);
    o.Area = ToRect(Area);
    o.Moveable = Moveable;
    o.Message = Str(sMessage);
    m_pRadarView->Building.push_back(o);
}

void CRadarScreen::RequestRefresh(void)
{
    if (!m_pRadarView) return;
    natc::Writer w(natc::Msg::RequestRefresh);
    w.I32(m_pRadarView->Id);
    Engine::Get().Send(w);
}

void CRadarScreen::ShowSectorFileElement(CSectorElement Element, const char* sComponentName, bool Show)
{
    if (!m_pRadarView || !Element.IsValid()) return;
    int type = Element.GetElementType();
    auto& all = Engine::Get().Elements;
    int index = CPlugInData::IndexOf(Element);
    if (type < 0 || type >= static_cast<int>(all.size()) || index < 0 || index >= static_cast<int>(all[static_cast<size_t>(type)].size())) return;
    natc::Writer w(natc::Msg::ShowSectorElement);
    w.I32(m_pRadarView->Id);
    w.I32(type);
    w.Str(natc::ToUtf8(all[static_cast<size_t>(type)][static_cast<size_t>(index)].Name.c_str()));
    w.Str(natc::ToUtf8(sComponentName));
    w.Bool(Show);
    Engine::Get().Send(w);
}

void CRadarScreen::RefreshMapContent(void)
{
    if (!m_pRadarView) return;
    natc::Writer w(natc::Msg::RefreshMap);
    w.I32(m_pRadarView->Id);
    Engine::Get().Send(w);
}

void CRadarScreen::StartTagFunction(const char* sCallsign, const char* sItemPlugInName, int ItemCode, const char* sItemString,
                                    const char* sFunctionPlugInName, int FunctionId, POINT Pt, RECT Area)
{
    auto& e = Engine::Get();
    // A function of a plugin is called right here, as EuroScope does; the built-in ones go to Network-ATC.
    if (sFunctionPlugInName != nullptr && *sFunctionPlugInName != 0)
    {
        for (auto& p : e.Plugins)
        {
            if (p->Instance == nullptr || p->Unloading || _stricmp(p->Name.c_str(), sFunctionPlugInName) != 0) continue;
            if (sCallsign) e.Asel = sCallsign;
            auto* saved = e.ContextPlugin;
            e.ContextPlugin = p.get();
            e.Guard(p.get(), "OnFunctionCall", [&] { p->Instance->OnFunctionCall(FunctionId, sItemString ? sItemString : "", Pt, Area); });
            e.ContextPlugin = saved;
            return;
        }
    }
    natc::Writer w(natc::Msg::StartTagFunction);
    w.Str(Str(sCallsign));
    w.Str(natc::ToUtf8(sItemPlugInName));
    w.I32(ItemCode);
    w.Str(natc::ToUtf8(sItemString));
    w.Str(natc::ToUtf8(sFunctionPlugInName));
    w.I32(FunctionId);
    w.I32(Pt.x);
    w.I32(Pt.y);
    w.R(ToRect(Area));
    e.Send(w);
}

void CRadarScreen::GetDisplayArea(CPosition* pLeftDown, CPosition* pRightUp)
{
    if (!m_pRadarView) return;
    if (pLeftDown) m_pRadarView->ToPosition(POINT{0, m_pRadarView->Height}, pLeftDown->m_Latitude, pLeftDown->m_Longitude);
    if (pRightUp) m_pRadarView->ToPosition(POINT{m_pRadarView->Width, 0}, pRightUp->m_Latitude, pRightUp->m_Longitude);
}

void CRadarScreen::SetDisplayArea(CPosition LeftDown, CPosition RightUp)
{
    if (!m_pRadarView) return;
    natc::Writer w(natc::Msg::SetDisplayArea);
    w.I32(m_pRadarView->Id);
    w.F64(LeftDown.m_Latitude);
    w.F64(LeftDown.m_Longitude);
    w.F64(RightUp.m_Latitude);
    w.F64(RightUp.m_Longitude);
    Engine::Get().Send(w);
}

// ---- CFlightPlanList ------------------------------------------------------------------------------------

#define LIST static_cast<natc::FpList*>(m_Position)

int CFlightPlanList::GetColumnNumber(void) { return m_Position ? static_cast<int>(LIST->Columns.size()) : 0; }

void CFlightPlanList::DeleteAllColumns(void)
{
    if (!m_Position) return;
    LIST->Columns.clear();
    LIST->Changed = true;
}

void CFlightPlanList::AddColumnDefinition(const char* sColumnTitle, int Width, bool Centered, const char* sItemProvifer, int ItemCode,
                                          const char* sLeftButtonFunctionProvifer, int LeftButtonFunction,
                                          const char* sRightButtonFunctionProvifer, int RightButtonFunction)
{
    if (!m_Position) return;
    natc::FpListColumn c;
    c.Title = Str(sColumnTitle);
    c.Width = Width;
    c.Centered = Centered;
    c.ItemPlugin = Str(sItemProvifer);
    c.ItemCode = ItemCode;
    c.LeftPlugin = Str(sLeftButtonFunctionProvifer);
    c.LeftFunction = LeftButtonFunction;
    c.RightPlugin = Str(sRightButtonFunctionProvifer);
    c.RightFunction = RightButtonFunction;
    LIST->Columns.push_back(c);
    LIST->Changed = true;
}

void CFlightPlanList::AddFpToTheList(CFlightPlan FlightPlan)
{
    auto* a = CPlugInData::Of(FlightPlan);
    if (!m_Position || a == nullptr) return;
    auto& cs = LIST->Callsigns;
    if (std::find(cs.begin(), cs.end(), a->Callsign) != cs.end()) return;
    cs.push_back(a->Callsign);
    LIST->Changed = true;
}

void CFlightPlanList::RemoveFpFromTheList(CFlightPlan FlightPlan)
{
    auto* a = CPlugInData::Of(FlightPlan);
    if (!m_Position || a == nullptr) return;
    auto& cs = LIST->Callsigns;
    auto it = std::find(cs.begin(), cs.end(), a->Callsign);
    if (it == cs.end()) return;
    cs.erase(it);
    LIST->Changed = true;
}

void CFlightPlanList::ShowFpList(bool Show)
{
    if (!m_Position || LIST->Visible == Show) return;
    LIST->Visible = Show;
    LIST->Changed = true;
}

// ---- CSectorElement -------------------------------------------------------------------------------------

namespace
{
    natc::SectorElement* ElementOf(int index, int type)
    {
        auto& all = Engine::Get().Elements;
        if (type < 0 || type >= static_cast<int>(all.size())) return nullptr;
        auto& list = all[static_cast<size_t>(type)];
        return index >= 0 && index < static_cast<int>(list.size()) ? &list[static_cast<size_t>(index)] : nullptr;
    }
}

#define ELEMENT ElementOf(m_Position, m_ElementType)

const char* CSectorElement::GetName(void) const
{
    auto* e = ELEMENT;
    return e ? e->Name.c_str() : Empty;
}

bool CSectorElement::GetPosition(CPosition* pPosition, int Index)
{
    auto* e = ELEMENT;
    if (e == nullptr || pPosition == nullptr || Index < 0 || Index >= static_cast<int>(e->Positions.size())) return false;
    pPosition->m_Latitude = e->Positions[static_cast<size_t>(Index)].first;
    pPosition->m_Longitude = e->Positions[static_cast<size_t>(Index)].second;
    return true;
}

const char* CSectorElement::GetComponentName(int Index)
{
    auto* e = ELEMENT;
    if (e == nullptr || Index < 0 || Index >= static_cast<int>(e->Components.size())) return nullptr;
    return e->Components[static_cast<size_t>(Index)].c_str();
}

double CSectorElement::GetFrequency(void) const
{
    auto* e = ELEMENT;
    return e ? e->Frequency : 0.0;
}

const char* CSectorElement::GetRunwayName(int Index) const
{
    auto* e = ELEMENT;
    if (e == nullptr || Index < 0 || Index > 1) return Empty;
    if (m_ElementType == SECTOR_ELEMENT_SID || m_ElementType == SECTOR_ELEMENT_STAR) return e->RunwayNames[0].c_str();
    return e->RunwayNames[static_cast<size_t>(Index)].c_str();
}

int CSectorElement::GetRunwayHeading(int Index) const
{
    auto* e = ELEMENT;
    if (e == nullptr || Index < 0 || Index > 1) return 0;
    return e->RunwayHeadings[static_cast<size_t>(Index)];
}

const char* CSectorElement::GetAirportName(void) const
{
    auto* e = ELEMENT;
    return e ? e->Airport.c_str() : Empty;
}

bool CSectorElement::IsElementActive(bool Departure, int Index)
{
    auto* e = ELEMENT;
    if (e == nullptr) return false;
    if (m_ElementType == SECTOR_ELEMENT_AIRPORT) return Departure ? e->Departure[0] : e->Arrival[0];
    if (Index < 0 || Index > 1) return false;
    return Departure ? e->Departure[static_cast<size_t>(Index)] : e->Arrival[static_cast<size_t>(Index)];
}

// ---- CGrountToAirChannel --------------------------------------------------------------------------------

namespace
{
    natc::Channel* ChannelOf(int index)
    {
        auto& all = Engine::Get().Channels;
        return index >= 0 && index < static_cast<int>(all.size()) ? &all[static_cast<size_t>(index)] : nullptr;
    }

    void Toggle(int index, const char* what)
    {
        natc::Aircraft* none = nullptr;
        Engine::Get().Act(natc::ActionKind::ChannelToggle, none, what, "", index);
    }
}

#define CH ChannelOf(m_Index)

const char* CGrountToAirChannel::GetName(void) { return CH ? CH->Name.c_str() : Empty; }
double CGrountToAirChannel::GetFrequency(void) { return CH ? CH->Frequency : 0.0; }
const char* CGrountToAirChannel::GetVoiceServer(void) { return Empty; }
const char* CGrountToAirChannel::GetVoiceChannel(void) { return Empty; }
bool CGrountToAirChannel::GetIsPrimary(void) { return CH && CH->Primary; }
bool CGrountToAirChannel::GetIsAtis(void) { return CH && CH->Atis; }
bool CGrountToAirChannel::GetIsTextReceiveOn(void) { return CH && CH->TextReceive; }
bool CGrountToAirChannel::GetIsTextTransmitOn(void) { return CH && CH->TextTransmit; }
bool CGrountToAirChannel::GetIsVoiceReceiveOn(void) { return CH && CH->VoiceReceive; }
bool CGrountToAirChannel::GetIsVoiceTransmitOn(void) { return CH && CH->VoiceTransmit; }
bool CGrountToAirChannel::GetIsVoiceConnected(void) { return CH && CH->VoiceConnected; }
void CGrountToAirChannel::TogglePrimary(void) { Toggle(m_Index, "primary"); }
void CGrountToAirChannel::ToggleAtis(void) { Toggle(m_Index, "atis"); }
void CGrountToAirChannel::ToggleTextReceive(void) { Toggle(m_Index, "textrx"); }
void CGrountToAirChannel::ToggleTextTransmit(void) { Toggle(m_Index, "texttx"); }
void CGrountToAirChannel::ToggleVoiceReceive(void) { Toggle(m_Index, "voicerx"); }
void CGrountToAirChannel::ToggleVoiceTransmit(void) { Toggle(m_Index, "voicetx"); }

// ---- CPlugIn --------------------------------------------------------------------------------------------

CPlugIn::CPlugIn(int CompatibilityCode, const char* sPlugInName, const char* sVersionNumber, const char* sAuthorName,
                 const char* sCopyrigthMessage)
{
    // The plugin is constructed inside EuroScopePlugInInit: the host prepared its record just before.
    auto& e = Engine::Get();
    CPlugInData* data = e.Loading;
    if (data == nullptr)
    {
        // A plugin creating a second CPlugIn on its own: give it a record so its calls work.
        auto extra = std::make_unique<CPlugInData>();
        extra->Id = static_cast<int>(e.Plugins.size()) + 1000;
        e.Plugins.push_back(std::move(extra));
        data = e.Plugins.back().get();
    }
    data->Compatibility = CompatibilityCode;
    data->Name = Str(sPlugInName);
    data->Version = Str(sVersionNumber);
    data->Author = Str(sAuthorName);
    data->Copyright = Str(sCopyrigthMessage);
    m_pPluginData = data;
}

CPlugIn::~CPlugIn(void)
{
    if (m_pPluginData != nullptr)
    {
        m_pPluginData->Instance = nullptr;
        m_pPluginData->Unloading = true;
    }
}

const char* CPlugIn::GetPlugInName(void) { return m_pPluginData ? m_pPluginData->Name.c_str() : Empty; }

void CPlugIn::RegisterDisplayType(const char* sDisplayName, bool NeedRadarContent, bool GeoReferenced, bool CanBeSaved, bool CanBeCreated)
{
    Engine::Get().SendDisplayType(m_pPluginData, sDisplayName, NeedRadarContent, GeoReferenced, CanBeSaved, CanBeCreated);
}

void CPlugIn::RegisterTagItemType(const char* sDisplayName, int Code)
{
    natc::Writer w(natc::Msg::TagItemType);
    w.I32(m_pPluginData ? m_pPluginData->Id : 0);
    w.Str(natc::ToUtf8(sDisplayName));
    w.I32(Code);
    Engine::Get().Send(w);
}

void CPlugIn::RegisterTagItemFunction(const char* sDisplayName, int Code)
{
    natc::Writer w(natc::Msg::TagItemFunction);
    w.I32(m_pPluginData ? m_pPluginData->Id : 0);
    w.Str(natc::ToUtf8(sDisplayName));
    w.I32(Code);
    Engine::Get().Send(w);
}

CFlightPlanList CPlugIn::RegisterFpList(const char* sListName)
{
    auto& e = Engine::Get();
    auto list = std::make_unique<natc::FpList>();
    list->Id = e.NextListId();
    list->PluginId = m_pPluginData ? m_pPluginData->Id : 0;
    list->Name = Str(sListName);
    e.Lists.push_back(std::move(list));
    return CPlugInData::List(e.Lists.back().get());
}

void CPlugIn::RegisterToolbarItem(int ItemId, const char* sItemName)
{
    // Toolbar items are not used by EuroScope any more; noted for the log only.
    Engine::Get().Log(GetPlugInName() + std::string(": toolbar item (") + Str(sItemName) + ", " + std::to_string(ItemId) + ") is not supported");
}

void CPlugIn::RefreshToolbar(bool ResizeToo) { (void)ResizeToo; }

void CPlugIn::SaveDataToSettings(const char* sVariableName, const char* sVariableDescription, const char* sValue)
{
    if (!m_pPluginData || sVariableName == nullptr) return;
    Engine::Get().Settings[m_pPluginData->Name][sVariableName] = {Str(sVariableDescription), Str(sValue)};
}

const char* CPlugIn::GetDataFromSettings(const char* sVariableName)
{
    if (!m_pPluginData || sVariableName == nullptr) return nullptr;
    auto& all = Engine::Get().Settings;
    auto p = all.find(m_pPluginData->Name);
    if (p == all.end()) return nullptr;
    auto v = p->second.find(sVariableName);
    return v == p->second.end() ? nullptr : v->second.second.c_str();
}

namespace
{
    natc::PopupTarget Owner(CPlugInData* plugin)
    {
        auto& e = Engine::Get();
        natc::PopupTarget t;
        t.Plugin = plugin;
        if (e.ContextView != nullptr && e.ContextScreen >= 0 && e.ContextPlugin == plugin)
        {
            t.ViewId = e.ContextView->Id;
            t.ScreenIndex = e.ContextScreen;
        }
        return t;
    }
}

void CPlugIn::OpenPopupEdit(RECT Area, int FunctionId, const char* sInitialValue)
{
    auto& e = Engine::Get();
    e.FlushPopup();
    e.PopupOpen = true;
    e.PopupIsEdit = true;
    e.PopupArea = ToRect(Area);
    e.PopupFunction = FunctionId;
    e.PopupInitial = Str(sInitialValue);
    e.PopupOwner = Owner(m_pPluginData);
}

void CPlugIn::OpenPopupList(RECT Area, const char* sTitle, int ColumnNumber)
{
    auto& e = Engine::Get();
    e.FlushPopup();
    e.PopupOpen = true;
    e.PopupIsEdit = false;
    e.PopupArea = ToRect(Area);
    e.PopupTitle = Str(sTitle);
    e.PopupColumns = ColumnNumber;
    e.PopupElements.clear();
    e.PopupOwner = Owner(m_pPluginData);
}

void CPlugIn::AddPopupListElement(const char* sString1, const char* sString2, int FunctionId, bool Selected, int Checked, bool Disabled, bool Fixed)
{
    auto& e = Engine::Get();
    if (!e.PopupOpen || e.PopupIsEdit) return;
    natc::PopupElement el;
    el.S1 = Str(sString1);
    el.S2 = Str(sString2);
    el.FunctionId = FunctionId;
    el.Selected = Selected;
    el.Checked = Checked;
    el.Disabled = Disabled;
    el.Fixed = Fixed;
    e.PopupElements.push_back(el);
}

int CPlugIn::GetConnectionType(void) const { return Engine::Get().ConnectionType; }
void CPlugIn::SelectActiveSectorfile(void) {}
void CPlugIn::SelectScreenSectorfile(CRadarScreen* pRadarScreen) { (void)pRadarScreen; }

void CPlugIn::SetASELAircraft(const CFlightPlan FlightPlan)
{
    auto* a = CPlugInData::Of(FlightPlan);
    if (a == nullptr) return;
    Engine::Get().Asel = a->Callsign;
    Engine::Get().Act(natc::ActionKind::SetAsel, a);
}

void CPlugIn::SetASELAircraft(const CRadarTarget RadarTarget)
{
    auto* a = CPlugInData::Of(RadarTarget);
    if (a == nullptr) return;
    Engine::Get().Asel = a->Callsign;
    Engine::Get().Act(natc::ActionKind::SetAsel, a);
}

namespace
{
    // Iteration order: the order in which aircraft appeared; gone ones and ones without the data are skipped.
    natc::Aircraft* NextAircraft(natc::Aircraft* after, bool flightPlan)
    {
        auto& order = Engine::Get().AircraftOrder;
        size_t i = 0;
        if (after != nullptr)
        {
            auto it = std::find(order.begin(), order.end(), after);
            if (it == order.end()) return nullptr;
            i = static_cast<size_t>(it - order.begin()) + 1;
        }
        for (; i < order.size(); i++)
        {
            auto* a = order[i];
            if (!a->Gone && (flightPlan ? a->HasFlightPlan : a->HasRadar)) return a;
        }
        return nullptr;
    }

    natc::Controller* NextController(natc::Controller* after)
    {
        auto& order = Engine::Get().ControllerOrder;
        size_t i = 0;
        if (after != nullptr)
        {
            auto it = std::find(order.begin(), order.end(), after);
            if (it == order.end()) return nullptr;
            i = static_cast<size_t>(it - order.begin()) + 1;
        }
        for (; i < order.size(); i++)
            if (!order[i]->Gone) return order[i];
        return nullptr;
    }
}

CFlightPlan CPlugIn::FlightPlanSelect(const char* sCallsign) const
{
    auto* a = Engine::Get().FindAircraft(sCallsign);
    return a && a->HasFlightPlan ? CPlugInData::Fp(a) : CFlightPlan();
}

CRadarTarget CPlugIn::RadarTargetSelect(const char* sCallsign) const
{
    auto* a = Engine::Get().FindAircraft(sCallsign);
    return a && a->HasRadar ? CPlugInData::Rt(a) : CRadarTarget();
}

CFlightPlan CPlugIn::FlightPlanSelectFirst(void) const
{
    auto* a = NextAircraft(nullptr, true);
    return a ? CPlugInData::Fp(a) : CFlightPlan();
}

CRadarTarget CPlugIn::RadarTargetSelectFirst(void) const
{
    auto* a = NextAircraft(nullptr, false);
    return a ? CPlugInData::Rt(a) : CRadarTarget();
}

CFlightPlan CPlugIn::FlightPlanSelectNext(CFlightPlan CurrentFlightPlan) const
{
    auto* current = CPlugInData::Of(CurrentFlightPlan);
    if (current == nullptr) return CFlightPlan();
    auto* a = NextAircraft(current, true);
    return a ? CPlugInData::Fp(a) : CFlightPlan();
}

CRadarTarget CPlugIn::RadarTargetSelectNext(CRadarTarget CurrentRadartarget) const
{
    auto* current = CPlugInData::Of(CurrentRadartarget);
    if (current == nullptr) return CRadarTarget();
    auto* a = NextAircraft(current, false);
    return a ? CPlugInData::Rt(a) : CRadarTarget();
}

CFlightPlan CPlugIn::FlightPlanSelectASEL(void) const
{
    auto& e = Engine::Get();
    auto* a = e.FindAircraft(e.Asel.c_str());
    return a && a->HasFlightPlan ? CPlugInData::Fp(a) : CFlightPlan();
}

CRadarTarget CPlugIn::RadarTargetSelectASEL(void) const
{
    auto& e = Engine::Get();
    auto* a = e.FindAircraft(e.Asel.c_str());
    return a && a->HasRadar ? CPlugInData::Rt(a) : CRadarTarget();
}

CController CPlugIn::ControllerSelect(const char* sCallsign) const
{
    auto& e = Engine::Get();
    if (sCallsign && _stricmp(sCallsign, e.Myself.Callsign.c_str()) == 0) return CPlugInData::Ctr(nullptr, true);
    auto* c = e.FindController(sCallsign);
    return c ? CPlugInData::Ctr(c) : CController();
}

CController CPlugIn::ControllerSelectByPositionId(const char* sPositionId) const
{
    auto& e = Engine::Get();
    if (sPositionId && !e.Myself.PositionId.empty() && _stricmp(sPositionId, e.Myself.PositionId.c_str()) == 0) return CPlugInData::Ctr(nullptr, true);
    auto* c = e.FindControllerById(sPositionId);
    return c ? CPlugInData::Ctr(c) : CController();
}

CController CPlugIn::ControllerMyself(void) const { return CPlugInData::Ctr(nullptr, true); }

CController CPlugIn::ControllerSelectFirst(void) const
{
    auto* c = NextController(nullptr);
    return c ? CPlugInData::Ctr(c) : CController();
}

CController CPlugIn::ControllerSelectNext(CController CurrentController) const
{
    auto* current = CPlugInData::Of(CurrentController);
    if (current == nullptr) return CController();
    auto* c = NextController(current);
    return c ? CPlugInData::Ctr(c) : CController();
}

CSectorElement CPlugIn::SectorFileElementSelectFirst(int ElementType) const
{
    return SectorFileElementSelectNext(CSectorElement(), ElementType);
}

CSectorElement CPlugIn::SectorFileElementSelectNext(CSectorElement CurrentElement, int ElementType) const
{
    auto& all = Engine::Get().Elements;
    int type = CurrentElement.IsValid() ? CurrentElement.GetElementType() : (ElementType == SECTOR_ELEMENT_ALL ? 0 : ElementType);
    int index = CurrentElement.IsValid() ? CPlugInData::IndexOf(CurrentElement) + 1 : 0;
    while (type >= 0 && type < static_cast<int>(all.size()))
    {
        if (index < static_cast<int>(all[static_cast<size_t>(type)].size())) return CPlugInData::Element(index, type);
        if (ElementType != SECTOR_ELEMENT_ALL) break;
        type++;
        index = 0;
    }
    return CSectorElement();
}

void CPlugIn::DisplayUserMessage(const char* sHandlerName, const char* sSenderName, const char* sMessage, bool ShowHandler, bool ShowUnread,
                                 bool ShowUnreadEvenIfBusy, bool StartFlashing, bool NeedConfirmation)
{
    natc::Writer w(natc::Msg::UserMessage);
    w.Str(natc::ToUtf8(sHandlerName));
    w.Str(natc::ToUtf8(sSenderName));
    w.Str(natc::ToUtf8(sMessage));
    w.Bool(ShowHandler);
    w.Bool(ShowUnread);
    w.Bool(ShowUnreadEvenIfBusy);
    w.Bool(StartFlashing);
    w.Bool(NeedConfirmation);
    Engine::Get().Send(w);
}

CGrountToAirChannel CPlugIn::GroundToArChannelSelectFirst(void)
{
    return Engine::Get().Channels.empty() ? CGrountToAirChannel() : CPlugInData::ChannelHandle(0);
}

CGrountToAirChannel CPlugIn::GroundToArChannelSelectNext(CGrountToAirChannel CurrentChannel)
{
    int next = CPlugInData::IndexOf(CurrentChannel) + 1;
    if (!CurrentChannel.IsValid() || next >= static_cast<int>(Engine::Get().Channels.size())) return CGrountToAirChannel();
    return CPlugInData::ChannelHandle(next);
}

void CPlugIn::AddAlias(const char* sAliasName, const char* sAliasValue)
{
    natc::Writer w(natc::Msg::Alias);
    w.Str(natc::ToUtf8(sAliasName));
    w.Str(natc::ToUtf8(sAliasValue));
    Engine::Get().Send(w);
}

int CPlugIn::GetTransitionAltitude(void) { return Engine::Get().TransitionAltitude; }

// ---- the host's entry point -----------------------------------------------------------------------------

extern "C" __declspec(dllexport) int NatcEsHostRun(const char* pipeName)
{
    return Engine::Get().Run(pipeName ? pipeName : "");
}
