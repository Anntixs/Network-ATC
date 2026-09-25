// The data classes of the interface: positions, flight plans, radar targets and what hangs off them.
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <ctime>

#include "Engine.h"

using namespace EuroScopePlugIn;
using natc::Aircraft;
using natc::ActionKind;
using natc::Engine;

namespace
{
    constexpr double Rad = 3.14159265358979323846 / 180.0;
    constexpr double EarthRadiusNm = 3440.065;

    // Returned for anything invalid: plugins get an empty string rather than a null pointer.
    const char* const Empty = "";

    std::string Str(const char* s) { return s ? std::string(s) : std::string(); }

    // "N055.58.20.000" / "E037.24.53.000", or a decimal number.
    bool ParseCoordinate(const char* text, bool latitude, double& out)
    {
        if (text == nullptr) return false;
        std::string s(text);
        while (!s.empty() && s.front() == ' ') s.erase(s.begin());
        if (s.empty()) return false;
        double sign = 1;
        char h = static_cast<char>(toupper(static_cast<unsigned char>(s[0])));
        if (h == 'N' || h == 'S' || h == 'E' || h == 'W')
        {
            if ((latitude && (h == 'E' || h == 'W')) || (!latitude && (h == 'N' || h == 'S'))) return false;
            if (h == 'S' || h == 'W') sign = -1;
            s.erase(s.begin());
        }
        int d = 0, m = 0;
        double sec = 0;
        if (sscanf(s.c_str(), "%d.%d.%lf", &d, &m, &sec) == 3)
        {
            out = sign * (d + m / 60.0 + sec / 3600.0);
            return true;
        }
        char* end = nullptr;
        double v = strtod(s.c_str(), &end);
        if (end == s.c_str()) return false;
        out = sign * v;
        return true;
    }

    Aircraft* A(const void* p) { return static_cast<Aircraft*>(const_cast<void*>(p)); }

    const natc::RadarPosition& PositionOf(void* rt, void* pos)
    {
        static natc::RadarPosition none;
        if (pos != nullptr) return *static_cast<natc::RadarPosition*>(pos);
        return rt ? A(rt)->FpTrack : none;
    }
}

// ---- CPosition ----------------------------------------------------------------------------------------

bool CPosition::LoadFromStrings(const char* sLongitude, const char* sLatitude)
{
    double lat = 0, lon = 0;
    if (!ParseCoordinate(sLatitude, true, lat) || !ParseCoordinate(sLongitude, false, lon)) return false;
    m_Latitude = lat;
    m_Longitude = lon;
    return true;
}

double CPosition::DistanceTo(const CPosition OtherPosition) const
{
    double lat1 = m_Latitude * Rad, lat2 = OtherPosition.m_Latitude * Rad;
    double dLat = lat2 - lat1, dLon = (OtherPosition.m_Longitude - m_Longitude) * Rad;
    double a = std::sin(dLat / 2) * std::sin(dLat / 2) + std::cos(lat1) * std::cos(lat2) * std::sin(dLon / 2) * std::sin(dLon / 2);
    return 2 * EarthRadiusNm * std::atan2(std::sqrt(a), std::sqrt(1 - a));
}

double CPosition::DirectionTo(const CPosition OtherPosition) const
{
    double lat1 = m_Latitude * Rad, lat2 = OtherPosition.m_Latitude * Rad, dLon = (OtherPosition.m_Longitude - m_Longitude) * Rad;
    double y = std::sin(dLon) * std::cos(lat2);
    double x = std::cos(lat1) * std::sin(lat2) - std::sin(lat1) * std::cos(lat2) * std::cos(dLon);
    double b = std::atan2(y, x) / Rad;
    return std::fmod(b + 360.0, 360.0);
}

// ---- CFlightPlanExtractedRoute --------------------------------------------------------------------------

int CFlightPlanExtractedRoute::GetPointsNumber(void) const { return m_FpPosition ? static_cast<int>(A(m_FpPosition)->Route_.size()) : 0; }
int CFlightPlanExtractedRoute::GetPointsCalculatedIndex(void) const { return m_FpPosition ? A(m_FpPosition)->RouteCalculatedIndex : -1; }
int CFlightPlanExtractedRoute::GetPointsAssignedIndex(void) const { return m_FpPosition ? A(m_FpPosition)->RouteAssignedIndex : -1; }

#define ROUTE_POINT(index) \
    (m_FpPosition != nullptr && (index) >= 0 && (index) < static_cast<int>(A(m_FpPosition)->Route_.size()) \
         ? &A(m_FpPosition)->Route_[static_cast<size_t>(index)] : nullptr)

const char* CFlightPlanExtractedRoute::GetPointName(int Index) const
{
    auto* p = ROUTE_POINT(Index);
    return p ? p->Name.c_str() : Empty;
}

CPosition CFlightPlanExtractedRoute::GetPointPosition(int Index) const
{
    CPosition pos;
    if (auto* p = ROUTE_POINT(Index))
    {
        pos.m_Latitude = p->Latitude;
        pos.m_Longitude = p->Longitude;
    }
    return pos;
}

const char* CFlightPlanExtractedRoute::GetPointAirwayName(int Index) const
{
    auto* p = ROUTE_POINT(Index);
    return p ? p->Airway.c_str() : Empty;
}

int CFlightPlanExtractedRoute::GetPointAirwayClassification(int Index) const
{
    auto* p = ROUTE_POINT(Index);
    return p ? p->AirwayClass : AIRWAY_CLASS_NO_DATA_DIRECT;
}

int CFlightPlanExtractedRoute::GetPointDistanceInMinutes(int Index) const
{
    auto* p = ROUTE_POINT(Index);
    return p ? p->Minutes : -1;
}

int CFlightPlanExtractedRoute::GetPointCalculatedProfileAltitude(int Index) const
{
    auto* p = ROUTE_POINT(Index);
    return p ? p->ProfileAltitude : 0;
}

// ---- CFlightPlanPositionPredictions ---------------------------------------------------------------------

#define PREDICTION(index) \
    (m_FpPosition != nullptr && (index) >= 0 && (index) < static_cast<int>(A(m_FpPosition)->Predictions.size()) \
         ? &A(m_FpPosition)->Predictions[static_cast<size_t>(index)] : nullptr)

int CFlightPlanPositionPredictions::GetPointsNumber(void) const { return m_FpPosition ? static_cast<int>(A(m_FpPosition)->Predictions.size()) : 0; }

CPosition CFlightPlanPositionPredictions::GetPosition(int Index) const
{
    CPosition pos;
    if (auto* p = PREDICTION(Index))
    {
        pos.m_Latitude = p->Latitude;
        pos.m_Longitude = p->Longitude;
    }
    return pos;
}

int CFlightPlanPositionPredictions::GetAltitude(int Index) const
{
    auto* p = PREDICTION(Index);
    return p ? p->Altitude : 0;
}

const char* CFlightPlanPositionPredictions::GetControllerId(int Index) const
{
    auto* p = PREDICTION(Index);
    return p ? p->ControllerId.c_str() : Empty;
}

// ---- CRadarTargetPositionData ---------------------------------------------------------------------------

int CRadarTargetPositionData::GetReceivedTime(void) const
{
    auto& p = PositionOf(m_RtPosition, m_PosPosition);
    if (p.ReceivedTime == 0) return 0;
    long long age = static_cast<long long>(time(nullptr)) - p.ReceivedTime;
    return age < 0 ? 0 : static_cast<int>(age);
}

CPosition CRadarTargetPositionData::GetPosition(void) const
{
    auto& p = PositionOf(m_RtPosition, m_PosPosition);
    CPosition pos;
    pos.m_Latitude = p.Latitude;
    pos.m_Longitude = p.Longitude;
    return pos;
}

const char* CRadarTargetPositionData::GetSquawk(void) const { return PositionOf(m_RtPosition, m_PosPosition).Squawk.c_str(); }
bool CRadarTargetPositionData::GetTransponderC(void) const { return PositionOf(m_RtPosition, m_PosPosition).ModeC; }
bool CRadarTargetPositionData::GetTransponderI(void) const { return PositionOf(m_RtPosition, m_PosPosition).Ident; }
int CRadarTargetPositionData::GetPressureAltitude(void) const { return PositionOf(m_RtPosition, m_PosPosition).PressureAltitude; }
int CRadarTargetPositionData::GetFlightLevel(void) const { return PositionOf(m_RtPosition, m_PosPosition).FlightLevel; }
int CRadarTargetPositionData::GetReportedGS(void) const { return PositionOf(m_RtPosition, m_PosPosition).GroundSpeed; }
int CRadarTargetPositionData::GetReportedHeading(void) const { return PositionOf(m_RtPosition, m_PosPosition).Heading; }
int CRadarTargetPositionData::GetReportedHeadingTrueNorth(void) const { return PositionOf(m_RtPosition, m_PosPosition).Heading; }
int CRadarTargetPositionData::GetReportedPitch(void) const { return PositionOf(m_RtPosition, m_PosPosition).Pitch; }
int CRadarTargetPositionData::GetReportedBank(void) const { return PositionOf(m_RtPosition, m_PosPosition).Bank; }
int CRadarTargetPositionData::GetRadarFlags(void) const { return PositionOf(m_RtPosition, m_PosPosition).RadarFlags; }

// ---- CFlightPlanData ------------------------------------------------------------------------------------

#define FPD A(m_FpPosition)
#define FPD_STR(field) (m_FpPosition ? FPD->field.c_str() : Empty)

namespace
{
    bool SetFpField(void* fp, std::string Aircraft::*field, const char* value, const char* name)
    {
        if (fp == nullptr) return false;
        auto* a = A(fp);
        a->*field = Str(value);
        Engine::Get().Act(ActionKind::SetFlightPlan, a, name, Str(value));
        return true;
    }
}

bool CFlightPlanData::IsReceived(void) const { return m_FpPosition && FPD->FpReceived; }
bool CFlightPlanData::IsAmended(void) const { return m_FpPosition && FPD->Amended; }

bool CFlightPlanData::AmendFlightPlan(void)
{
    if (!m_FpPosition) return false;
    FPD->Amended = true;
    Engine::Get().Act(ActionKind::AmendFlightPlan, FPD);
    Engine::Get().FireFlightPlanDataUpdate(FPD);
    return true;
}

const char* CFlightPlanData::GetPlanType(void) const { return FPD_STR(PlanType); }
bool CFlightPlanData::SetPlanType(const char* sPlanType) { return SetFpField(m_FpPosition, &Aircraft::PlanType, sPlanType, "PlanType"); }
const char* CFlightPlanData::GetAircraftInfo(void) const { return FPD_STR(AircraftInfo); }
bool CFlightPlanData::SetAircraftInfo(const char* sInfo) { return SetFpField(m_FpPosition, &Aircraft::AircraftInfo, sInfo, "AircraftInfo"); }
char CFlightPlanData::GetAircraftWtc(void) const { return m_FpPosition ? FPD->Wtc : '?'; }
char CFlightPlanData::GetAircraftType(void) const { return m_FpPosition ? FPD->AircraftType : '?'; }
int CFlightPlanData::GetEngineNumber(void) const { return m_FpPosition ? FPD->Engines : 0; }
char CFlightPlanData::GetEngineType(void) const { return m_FpPosition ? FPD->EngineType : '?'; }
char CFlightPlanData::GetCapibilities(void) const { return m_FpPosition ? FPD->Capabilities : '?'; }
bool CFlightPlanData::IsRvsm(void) const { return m_FpPosition && FPD->Rvsm; }
const char* CFlightPlanData::GetManufacturerType(void) const { return FPD_STR(Manufacturer); }
const char* CFlightPlanData::GetAircraftFPType(void) const { return FPD_STR(AircraftFpType); }
int CFlightPlanData::GetTrueAirspeed(void) const { return m_FpPosition ? FPD->TrueAirspeed : 0; }

bool CFlightPlanData::SetTrueAirspeed(int TrueAirspeed)
{
    if (!m_FpPosition) return false;
    FPD->TrueAirspeed = TrueAirspeed;
    Engine::Get().Act(ActionKind::SetFlightPlan, FPD, "TrueAirspeed", std::to_string(TrueAirspeed));
    return true;
}

const char* CFlightPlanData::GetOrigin(void) const { return FPD_STR(Origin); }
bool CFlightPlanData::SetOrigin(const char* sOrigin) { return SetFpField(m_FpPosition, &Aircraft::Origin, sOrigin, "Origin"); }
int CFlightPlanData::GetFinalAltitude(void) const { return m_FpPosition ? FPD->FinalAltitude : 0; }

bool CFlightPlanData::SetFinalAltitude(int FinalAltitude)
{
    if (!m_FpPosition) return false;
    FPD->FinalAltitude = FinalAltitude;
    Engine::Get().Act(ActionKind::SetFlightPlan, FPD, "FinalAltitude", std::to_string(FinalAltitude));
    return true;
}

const char* CFlightPlanData::GetDestination(void) const { return FPD_STR(Destination); }
bool CFlightPlanData::SetDestination(const char* sDestination) { return SetFpField(m_FpPosition, &Aircraft::Destination, sDestination, "Destination"); }
const char* CFlightPlanData::GetAlternate(void) const { return FPD_STR(Alternate); }
bool CFlightPlanData::SetAlternate(const char* sAlternate) { return SetFpField(m_FpPosition, &Aircraft::Alternate, sAlternate, "Alternate"); }
const char* CFlightPlanData::GetRemarks(void) const { return FPD_STR(Remarks); }
bool CFlightPlanData::SetRemarks(const char* sRemarks) { return SetFpField(m_FpPosition, &Aircraft::Remarks, sRemarks, "Remarks"); }
char CFlightPlanData::GetCommunicationType(void) const { return m_FpPosition ? FPD->CommunicationType : '?'; }
const char* CFlightPlanData::GetRoute(void) const { return FPD_STR(Route); }
bool CFlightPlanData::SetRoute(const char* sRoute) { return SetFpField(m_FpPosition, &Aircraft::Route, sRoute, "Route"); }
const char* CFlightPlanData::GetSidName(void) const { return FPD_STR(Sid); }
const char* CFlightPlanData::GetStarName(void) const { return FPD_STR(Star); }
const char* CFlightPlanData::GetDepartureRwy(void) const { return FPD_STR(DepartureRwy); }
const char* CFlightPlanData::GetArrivalRwy(void) const { return FPD_STR(ArrivalRwy); }
const char* CFlightPlanData::GetEstimatedDepartureTime(void) const { return FPD_STR(EstimatedDeparture); }
bool CFlightPlanData::SetEstimatedDepartureTime(const char* sDepTime) { return SetFpField(m_FpPosition, &Aircraft::EstimatedDeparture, sDepTime, "EstimatedDeparture"); }
const char* CFlightPlanData::GetActualDepartureTime(void) const { return FPD_STR(ActualDeparture); }
bool CFlightPlanData::SetActualDepartureTime(const char* sDepTime) { return SetFpField(m_FpPosition, &Aircraft::ActualDeparture, sDepTime, "ActualDeparture"); }
const char* CFlightPlanData::GetEnrouteHours(void) const { return FPD_STR(EnrouteHours); }
bool CFlightPlanData::SetEnrouteHours(const char* s) { return SetFpField(m_FpPosition, &Aircraft::EnrouteHours, s, "EnrouteHours"); }
const char* CFlightPlanData::GetEnrouteMinutes(void) const { return FPD_STR(EnrouteMinutes); }
bool CFlightPlanData::SetEnrouteMinutes(const char* s) { return SetFpField(m_FpPosition, &Aircraft::EnrouteMinutes, s, "EnrouteMinutes"); }
const char* CFlightPlanData::GetFuelHours(void) const { return FPD_STR(FuelHours); }
bool CFlightPlanData::SetFuelHours(const char* s) { return SetFpField(m_FpPosition, &Aircraft::FuelHours, s, "FuelHours"); }
const char* CFlightPlanData::GetFuelMinutes(void) const { return FPD_STR(FuelMinutes); }
bool CFlightPlanData::SetFuelMinutes(const char* s) { return SetFpField(m_FpPosition, &Aircraft::FuelMinutes, s, "FuelMinutes"); }

// A simple performance model: jets by altitude, props slower.
int CFlightPlanData::PerformanceGetIas(int Altitude, int VerticalSpeed)
{
    bool jet = !m_FpPosition || FPD->EngineType == 'J';
    if (!jet) return Altitude < 10000 ? 140 : 180;
    int ias = Altitude < 10000 ? 250 : Altitude < 28000 ? 290 : 270;
    if (VerticalSpeed < 0 && Altitude < 5000) ias = 180;
    return ias;
}

int CFlightPlanData::PerformanceGetMach(int Altitude, int VerticalSpeed)
{
    (void)VerticalSpeed;
    bool jet = !m_FpPosition || FPD->EngineType == 'J';
    return jet && Altitude >= 28000 ? 78 : 0;
}

int CFlightPlanData::PerformanceGetClimbRate(int Altitude)
{
    bool jet = !m_FpPosition || FPD->EngineType == 'J';
    if (!jet) return 800;
    return Altitude < 10000 ? 2500 : Altitude < 25000 ? 1800 : 1000;
}

int CFlightPlanData::PerformanceGetDescentRate(int Altitude)
{
    bool jet = !m_FpPosition || FPD->EngineType == 'J';
    if (!jet) return 700;
    return Altitude < 10000 ? 1500 : 2200;
}

// ---- CFlightPlanControllerAssignedData ------------------------------------------------------------------

namespace
{
    bool Assign(void* fp, int dataType, const std::string& value)
    {
        if (fp == nullptr) return false;
        auto* a = A(fp);
        Engine::Get().Act(ActionKind::SetAssigned, a, value, "", dataType);
        Engine::Get().FireAssignedDataUpdate(a, dataType);
        return true;
    }
}

const char* CFlightPlanControllerAssignedData::GetSquawk(void) const { return FPD_STR(AssignedSquawk); }

bool CFlightPlanControllerAssignedData::SetSquawk(const char* sSquawk)
{
    if (!m_FpPosition) return false;
    FPD->AssignedSquawk = Str(sSquawk);
    return Assign(m_FpPosition, CTR_DATA_TYPE_SQUAWK, FPD->AssignedSquawk);
}

int CFlightPlanControllerAssignedData::GetFinalAltitude(void) const { return m_FpPosition ? FPD->AssignedFinalAltitude : 0; }

bool CFlightPlanControllerAssignedData::SetFinalAltitude(int FinalAltitude)
{
    if (!m_FpPosition) return false;
    FPD->AssignedFinalAltitude = FinalAltitude;
    return Assign(m_FpPosition, CTR_DATA_TYPE_FINAL_ALTITUDE, std::to_string(FinalAltitude));
}

int CFlightPlanControllerAssignedData::GetClearedAltitude(void) const { return m_FpPosition ? FPD->ClearedAltitude : 0; }

bool CFlightPlanControllerAssignedData::SetClearedAltitude(int ClearedAltitude)
{
    if (!m_FpPosition) return false;
    FPD->ClearedAltitude = ClearedAltitude;
    return Assign(m_FpPosition, CTR_DATA_TYPE_TEMPORARY_ALTITUDE, std::to_string(ClearedAltitude));
}

char CFlightPlanControllerAssignedData::GetCommunicationType(void) const { return m_FpPosition ? FPD->AssignedCommunicationType : ' '; }

bool CFlightPlanControllerAssignedData::SetCommunicationType(char CommunicationType)
{
    if (!m_FpPosition) return false;
    FPD->AssignedCommunicationType = CommunicationType;
    return Assign(m_FpPosition, CTR_DATA_TYPE_COMMUNICATION_TYPE, std::string(1, CommunicationType));
}

const char* CFlightPlanControllerAssignedData::GetScratchPadString(void) const { return FPD_STR(ScratchPad); }

bool CFlightPlanControllerAssignedData::SetScratchPadString(const char* sString)
{
    if (!m_FpPosition) return false;
    FPD->ScratchPad = Str(sString);
    return Assign(m_FpPosition, CTR_DATA_TYPE_SCRATCH_PAD_STRING, FPD->ScratchPad);
}

int CFlightPlanControllerAssignedData::GetAssignedSpeed(void) const { return m_FpPosition ? FPD->AssignedSpeed : 0; }

bool CFlightPlanControllerAssignedData::SetAssignedSpeed(int AssignedSpeed)
{
    if (!m_FpPosition) return false;
    FPD->AssignedSpeed = AssignedSpeed;
    return Assign(m_FpPosition, CTR_DATA_TYPE_SPEED, std::to_string(AssignedSpeed));
}

int CFlightPlanControllerAssignedData::GetAssignedMach(void) const { return m_FpPosition ? FPD->AssignedMach : 0; }

bool CFlightPlanControllerAssignedData::SetAssignedMach(int AssignedMach)
{
    if (!m_FpPosition) return false;
    FPD->AssignedMach = AssignedMach;
    return Assign(m_FpPosition, CTR_DATA_TYPE_MACH, std::to_string(AssignedMach));
}

int CFlightPlanControllerAssignedData::GetAssignedRate(void) const { return m_FpPosition ? FPD->AssignedRate : 0; }

bool CFlightPlanControllerAssignedData::SetAssignedRate(int AssignedRate)
{
    if (!m_FpPosition) return false;
    FPD->AssignedRate = AssignedRate;
    return Assign(m_FpPosition, CTR_DATA_TYPE_RATE, std::to_string(AssignedRate));
}

int CFlightPlanControllerAssignedData::GetAssignedHeading(void) const { return m_FpPosition ? FPD->AssignedHeading : 0; }

bool CFlightPlanControllerAssignedData::SetAssignedHeading(int AssignedHeading)
{
    if (!m_FpPosition) return false;
    FPD->AssignedHeading = AssignedHeading;
    return Assign(m_FpPosition, CTR_DATA_TYPE_HEADING, std::to_string(AssignedHeading));
}

const char* CFlightPlanControllerAssignedData::GetDirectToPointName(void) const { return FPD_STR(DirectTo); }

bool CFlightPlanControllerAssignedData::SetDirectToPointName(const char* sPointName)
{
    if (!m_FpPosition) return false;
    FPD->DirectTo = Str(sPointName);
    return Assign(m_FpPosition, CTR_DATA_TYPE_DIRECT_TO, FPD->DirectTo);
}

const char* CFlightPlanControllerAssignedData::GetFlightStripAnnotation(int Index) const
{
    if (!m_FpPosition || Index < 0 || Index > 8) return Empty;
    return FPD->Annotations[static_cast<size_t>(Index)].c_str();
}

bool CFlightPlanControllerAssignedData::SetFlightStripAnnotation(int Index, const char* sAnnotation)
{
    if (!m_FpPosition || Index < 0 || Index > 8) return false;
    FPD->Annotations[static_cast<size_t>(Index)] = Str(sAnnotation);
    Engine::Get().Act(ActionKind::SetAnnotation, FPD, Str(sAnnotation), "", Index);
    return true;
}

// ---- CFlightPlan ----------------------------------------------------------------------------------------

#define FP A(m_FpPosition)
#define FP_STR(field) (m_FpPosition ? FP->field.c_str() : Empty)

const char* CFlightPlan::GetCallsign(void) const { return FP_STR(Callsign); }
const char* CFlightPlan::GetPilotName(void) const { return FP_STR(PilotName); }
int CFlightPlan::GetState(void) const { return m_FpPosition ? FP->State : FLIGHT_PLAN_STATE_NON_CONCERNED; }
int CFlightPlan::GetFPState(void) const { return m_FpPosition ? FP->FpState : 0; }
bool CFlightPlan::GetSimulated(void) const { return m_FpPosition && FP->Simulated; }
const char* CFlightPlan::GetTrackingControllerCallsign(void) const { return FP_STR(TrackingCallsign); }
const char* CFlightPlan::GetTrackingControllerId(void) const { return FP_STR(TrackingId); }
bool CFlightPlan::GetTrackingControllerIsMe(void) const { return m_FpPosition && FP->TrackingIsMe; }
const char* CFlightPlan::GetHandoffTargetControllerCallsign(void) const { return FP_STR(HandoffCallsign); }
const char* CFlightPlan::GetHandoffTargetControllerId(void) const { return FP_STR(HandoffId); }
double CFlightPlan::GetDistanceToDestination(void) const { return m_FpPosition ? FP->DistanceToDestination : 0; }
double CFlightPlan::GetDistanceFromOrigin(void) const { return m_FpPosition ? FP->DistanceFromOrigin : 0; }
const char* CFlightPlan::GetNextCopxPointName(void) const { return FP_STR(NextCopx); }
const char* CFlightPlan::GetNextFirCopxPointName(void) const { return FP_STR(NextFirCopx); }
int CFlightPlan::GetSectorEntryMinutes(void) const { return m_FpPosition ? FP->SectorEntryMinutes : -1; }
int CFlightPlan::GetSectorExitMinutes(void) const { return m_FpPosition ? FP->SectorExitMinutes : -1; }
bool CFlightPlan::GetRAMFlag(void) const { return m_FpPosition && FP->Ram; }
bool CFlightPlan::GetCLAMFlag(void) const { return m_FpPosition && FP->Clam; }
const char* CFlightPlan::GetGroundState(void) const { return FP_STR(GroundState); }
bool CFlightPlan::GetClearenceFlag(void) const { return m_FpPosition && FP->Clearance; }
bool CFlightPlan::IsTextCommunication(void) const { return m_FpPosition && FP->TextCommunication; }
int CFlightPlan::GetFinalAltitude(void) const
{
    if (!m_FpPosition) return 0;
    return FP->AssignedFinalAltitude != 0 ? FP->AssignedFinalAltitude : FP->FinalAltitude;
}
int CFlightPlan::GetClearedAltitude(void) const
{
    if (!m_FpPosition) return 0;
    return FP->ClearedAltitude != 0 ? FP->ClearedAltitude : GetFinalAltitude();
}
int CFlightPlan::GetEntryCoordinationPointState(void) const { return m_FpPosition ? FP->EntryPointState : COORDINATION_STATE_NONE; }
const char* CFlightPlan::GetEntryCoordinationPointName(void) const { return FP_STR(EntryPoint); }
int CFlightPlan::GetEntryCoordinationAltitudeState(void) const { return m_FpPosition ? FP->EntryAltitudeState : COORDINATION_STATE_NONE; }
int CFlightPlan::GetEntryCoordinationAltitude(void) const { return m_FpPosition ? FP->EntryAltitude : 0; }
int CFlightPlan::GetExitCoordinationNameState(void) const { return m_FpPosition ? FP->ExitPointState : COORDINATION_STATE_NONE; }
const char* CFlightPlan::GetExitCoordinationPointName(void) const { return FP_STR(ExitPoint); }
int CFlightPlan::GetExitCoordinationAltitudeState(void) const { return m_FpPosition ? FP->ExitAltitudeState : COORDINATION_STATE_NONE; }
int CFlightPlan::GetExitCoordinationAltitude(void) const { return m_FpPosition ? FP->ExitAltitude : 0; }
const char* CFlightPlan::GetCoordinatedNextController(void) const { return FP_STR(CoordinatedNextController); }
int CFlightPlan::GetCoordinatedNextControllerState(void) const { return m_FpPosition ? FP->CoordinatedNextControllerState : COORDINATION_STATE_NONE; }

CRadarTarget CFlightPlan::GetCorrelatedRadarTarget(void) const
{
    CRadarTarget rt;
    if (m_FpPosition && FP->HasRadar && FP->Correlated) rt.m_RtPosition = m_FpPosition;
    return rt;
}

bool CFlightPlan::CorrelateWithRadarTarget(CRadarTarget RadarTarget)
{
    if (!m_FpPosition || RadarTarget.m_RtPosition == nullptr) return false;
    FP->Correlated = true;
    Engine::Get().Act(ActionKind::Correlate, FP, A(RadarTarget.m_RtPosition)->Callsign);
    return true;
}

void CFlightPlan::Uncorrelate(void)
{
    if (!m_FpPosition) return;
    FP->Correlated = false;
    Engine::Get().Act(ActionKind::Uncorrelate, FP);
}

bool CFlightPlan::StartTracking(void)
{
    if (!m_FpPosition) return false;
    auto& me = Engine::Get().Myself;
    if (!FP->TrackingCallsign.empty() && !FP->TrackingIsMe) return false;
    FP->TrackingIsMe = true;
    FP->TrackingCallsign = me.Callsign;
    FP->TrackingId = me.PositionId;
    FP->State = FLIGHT_PLAN_STATE_ASSUMED;
    Engine::Get().Act(ActionKind::StartTracking, FP);
    return true;
}

bool CFlightPlan::EndTracking(void)
{
    if (!m_FpPosition || !FP->TrackingIsMe) return false;
    FP->TrackingIsMe = false;
    FP->TrackingCallsign.clear();
    FP->TrackingId.clear();
    FP->State = FLIGHT_PLAN_STATE_NON_CONCERNED;
    Engine::Get().Act(ActionKind::EndTracking, FP);
    return true;
}

bool CFlightPlan::InitiateHandoff(const char* sTargetController)
{
    if (!m_FpPosition || !FP->TrackingIsMe || sTargetController == nullptr) return false;
    FP->HandoffCallsign = sTargetController;
    if (auto* c = Engine::Get().FindController(sTargetController)) FP->HandoffId = c->PositionId;
    FP->State = FLIGHT_PLAN_STATE_TRANSFER_FROM_ME_INITIATED;
    Engine::Get().Act(ActionKind::InitiateHandoff, FP, sTargetController);
    return true;
}

void CFlightPlan::AcceptHandoff(void)
{
    if (!m_FpPosition) return;
    Engine::Get().Act(ActionKind::AcceptHandoff, FP);
}

void CFlightPlan::RefuseHandoff(void)
{
    if (!m_FpPosition) return;
    Engine::Get().Act(ActionKind::RefuseHandoff, FP);
}

bool CFlightPlan::InitiateCoordination(const char* sTargetController, const char* sPointName, int Altitude)
{
    if (!m_FpPosition) return false;
    Engine::Get().Act(ActionKind::InitiateCoordination, FP, Str(sTargetController), Str(sPointName), Altitude);
    return true;
}

void CFlightPlan::AcceptCoordination(void)
{
    if (m_FpPosition) Engine::Get().Act(ActionKind::AcceptCoordination, FP);
}

void CFlightPlan::RefuseCoordination(void)
{
    if (m_FpPosition) Engine::Get().Act(ActionKind::RefuseCoordination, FP);
}

void CFlightPlan::PushFlightStrip(const char* sTargetController)
{
    if (m_FpPosition) Engine::Get().Act(ActionKind::PushStrip, FP, Str(sTargetController));
}

void CFlightPlan::SetEstimation(const char* sPointName, const char* sTime)
{
    if (m_FpPosition) Engine::Get().Act(ActionKind::SetEstimation, FP, Str(sPointName), Str(sTime));
}

void CFlightPlan::ClearEstimation(void)
{
    if (m_FpPosition) Engine::Get().Act(ActionKind::ClearEstimation, FP);
}

void CFlightPlan::ClearEstimation(const char* sPointName)
{
    if (m_FpPosition) Engine::Get().Act(ActionKind::ClearEstimation, FP, Str(sPointName));
}

CFlightPlanExtractedRoute CFlightPlan::GetExtractedRoute(void) const
{
    CFlightPlanExtractedRoute r;
    r.m_FpPosition = m_FpPosition;
    return r;
}

CFlightPlanPositionPredictions CFlightPlan::GetPositionPredictions(void) const
{
    CFlightPlanPositionPredictions p;
    p.m_FpPosition = m_FpPosition;
    return p;
}

CRadarTargetPositionData CFlightPlan::GetFPTrackPosition(void) const
{
    CRadarTargetPositionData p;
    p.m_RtPosition = m_FpPosition;
    p.m_PosPosition = nullptr;
    return p;
}

CFlightPlanData CFlightPlan::GetFlightPlanData(void) const
{
    CFlightPlanData d;
    d.m_FpPosition = m_FpPosition;
    return d;
}

CFlightPlanControllerAssignedData CFlightPlan::GetControllerAssignedData(void) const
{
    CFlightPlanControllerAssignedData d;
    d.m_FpPosition = m_FpPosition;
    return d;
}

// ---- CRadarTarget ---------------------------------------------------------------------------------------

#define RT A(m_RtPosition)

const char* CRadarTarget::GetCallsign(void) const { return m_RtPosition ? RT->Callsign.c_str() : Empty; }
const char* CRadarTarget::GetSystemID(void) const { return m_RtPosition ? RT->SystemId.c_str() : Empty; }
int CRadarTarget::GetVerticalSpeed(void) const { return m_RtPosition ? RT->VerticalSpeed : 0; }
double CRadarTarget::GetTrackHeading(void) const { return m_RtPosition ? RT->TrackHeading : 0; }
int CRadarTarget::GetGS(void) const { return m_RtPosition ? RT->Current().GroundSpeed : 0; }

CFlightPlan CRadarTarget::GetCorrelatedFlightPlan(void) const
{
    CFlightPlan fp;
    if (m_RtPosition && RT->HasFlightPlan && RT->Correlated) fp.m_FpPosition = m_RtPosition;
    return fp;
}

bool CRadarTarget::CorrelateWithFlightPlan(CFlightPlan FlightPlan)
{
    if (!m_RtPosition || FlightPlan.m_FpPosition == nullptr) return false;
    RT->Correlated = true;
    Engine::Get().Act(ActionKind::Correlate, A(FlightPlan.m_FpPosition), RT->Callsign);
    return true;
}

void CRadarTarget::Uncorrelate(void)
{
    if (!m_RtPosition) return;
    RT->Correlated = false;
    Engine::Get().Act(ActionKind::Uncorrelate, RT);
}

CRadarTargetPositionData CRadarTarget::GetPosition(void) const
{
    CRadarTargetPositionData p;
    if (m_RtPosition && RT->HasRadar && RT->Current().Valid)
    {
        p.m_RtPosition = m_RtPosition;
        p.m_PosPosition = &RT->History[static_cast<size_t>(RT->Newest)];
    }
    return p;
}

CRadarTargetPositionData CRadarTarget::GetPreviousPosition(const CRadarTargetPositionData CurrentPosition) const
{
    CRadarTargetPositionData p;
    if (!m_RtPosition || CurrentPosition.m_PosPosition == nullptr) return p;
    auto* a = RT;
    auto* current = static_cast<natc::RadarPosition*>(CurrentPosition.m_PosPosition);
    int index = static_cast<int>(current - a->History.data());
    if (index < 0 || index >= Aircraft::HistorySize) return p;
    int previous = (index + 1) % Aircraft::HistorySize;
    if (previous == a->Newest || !a->History[static_cast<size_t>(previous)].Valid) return p;
    p.m_RtPosition = m_RtPosition;
    p.m_PosPosition = &a->History[static_cast<size_t>(previous)];
    return p;
}
