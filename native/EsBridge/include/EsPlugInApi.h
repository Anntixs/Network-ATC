// Network-ATC: the EuroScope plugin interface, declared so that plugins built for EuroScope load in
// Network-ATC unchanged. The classes, their member order, virtual functions and data members must stay
// exactly as they are: plugins are compiled against them (object sizes, vtables and decorated names).
// Built with NATC_ES_BRIDGE defined, the classes are exported by our EuroScopePlugInDll.dll.
#pragma once

#include <windows.h>

#ifdef NATC_ES_BRIDGE
#define NATC_ES_API __declspec(dllexport)
#else
#define NATC_ES_API __declspec(dllimport)
#endif

// Implementation classes of the host; the interface classes grant them access to their handles.
class CRadarView;
class CPlugInData;

namespace EuroScopePlugIn
{
    constexpr int COMPATIBILITY_CODE = 16;
    constexpr int FLIGHT_PLAN_STATE_NOT_STARTED = 0;
    constexpr int FLIGHT_PLAN_STATE_SIMULATED = 1;
    constexpr int FLIGHT_PLAN_STATE_TERMINATED = 2;
    constexpr int FLIGHT_PLAN_STATE_NON_CONCERNED = 0;
    constexpr int FLIGHT_PLAN_STATE_NOTIFIED = 1;
    constexpr int FLIGHT_PLAN_STATE_COORDINATED = 2;
    constexpr int FLIGHT_PLAN_STATE_TRANSFER_TO_ME_INITIATED = 3;
    constexpr int FLIGHT_PLAN_STATE_TRANSFER_FROM_ME_INITIATED = 4;
    constexpr int FLIGHT_PLAN_STATE_ASSUMED = 5;
    constexpr int FLIGHT_PLAN_STATE_REDUNDANT = 7;
    constexpr int AIRWAY_CLASS_VALID = 0;
    constexpr int AIRWAY_CLASS_DIRECTION_ERROR = 1;
    constexpr int AIRWAY_CLASS_UNCONNECTED = 2;
    constexpr int AIRWAY_CLASS_NO_DATA_DIRECT = 3;
    constexpr int CTR_DATA_TYPE_SQUAWK = 1;
    constexpr int CTR_DATA_TYPE_FINAL_ALTITUDE = 2;
    constexpr int CTR_DATA_TYPE_TEMPORARY_ALTITUDE = 3;
    constexpr int CTR_DATA_TYPE_COMMUNICATION_TYPE = 4;
    constexpr int CTR_DATA_TYPE_SCRATCH_PAD_STRING = 5;
    constexpr int CTR_DATA_TYPE_GROUND_STATE = 6;
    constexpr int CTR_DATA_TYPE_CLEARENCE_FLAG = 7;
    constexpr int CTR_DATA_TYPE_DEPARTURE_SEQUENCE = 8;
    constexpr int CTR_DATA_TYPE_SPEED = 9;
    constexpr int CTR_DATA_TYPE_MACH = 10;
    constexpr int CTR_DATA_TYPE_RATE = 11;
    constexpr int CTR_DATA_TYPE_HEADING = 12;
    constexpr int CTR_DATA_TYPE_DIRECT_TO = 13;
    constexpr int REFRESH_PHASE_BACK_BITMAP = 0;
    constexpr int REFRESH_PHASE_BEFORE_TAGS = 1;
    constexpr int REFRESH_PHASE_AFTER_TAGS = 2;
    constexpr int REFRESH_PHASE_AFTER_LISTS = 3;
    constexpr int TAG_COLOR_DEFAULT = 0;
    constexpr int TAG_COLOR_RGB_DEFINED = 1;
    constexpr int TAG_COLOR_NON_CONCERNED = 2;
    constexpr int TAG_COLOR_NOTIFIED = 3;
    constexpr int TAG_COLOR_ASSUMED = 4;
    constexpr int TAG_COLOR_TRANSFER_TO_ME_INITIATED = 5;
    constexpr int TAG_COLOR_REDUNDANT = 6;
    constexpr int TAG_COLOR_INFORMATION = 7;
    constexpr int TAG_COLOR_ONGOING_REQUEST_FROM_ME = 8;
    constexpr int TAG_COLOR_ONGOING_REQUEST_TO_ME = 9;
    constexpr int TAG_COLOR_ONGOING_REQUEST_ACCEPTED = 10;
    constexpr int TAG_COLOR_ONGOING_REQUEST_REFUSED = 11;
    constexpr int TAG_COLOR_EMERGENCY = 12;
    constexpr int TAG_TYPE_UNTAGGED = 0;
    constexpr int TAG_TYPE_TAGGED = 1;
    constexpr int TAG_TYPE_DETAILED = 2;
    constexpr int TAG_TYPE_TSSR = 3;
    constexpr int TAG_ITEM_TYPE_NEXT_LINE = 0;
    constexpr int TAG_ITEM_TYPE_STATIC_STRING = 1;
    constexpr int TAG_ITEM_TYPE_SQUAWK = 2;
    constexpr int TAG_ITEM_TYPE_VERTICAL_SPEED_INDICATOR = 3;
    constexpr int TAG_ITEM_TYPE_ALTITUDE = 4;
    constexpr int TAG_ITEM_TYPE_EMERGENCY_INDICATOR = 5;
    constexpr int TAG_ITEM_TYPE_RADIO_FAILURE_INDICATOR = 6;
    constexpr int TAG_ITEM_TYPE_HIJACK_INDICATOR = 7;
    constexpr int TAG_ITEM_TYPE_COLLOSION_ALERT = 8;
    constexpr int TAG_ITEM_TYPE_CALLSIGN = 9;
    constexpr int TAG_ITEM_TYPE_AIRCRAFT_CATEGORY = 10;
    constexpr int TAG_ITEM_TYPE_COMMUNICATION_TYPE = 11;
    constexpr int TAG_ITEM_TYPE_VERTICAL_SPEED = 12;
    constexpr int TAG_ITEM_TYPE_GROUND_SPEED_WITH_N = 13;
    constexpr int TAG_ITEM_TYPE_HANDOFF_TARGET = 14;
    constexpr int TAG_ITEM_TYPE_OWNER = 15;
    constexpr int TAG_ITEM_TYPE_PLANE_TYPE = 16;
    constexpr int TAG_ITEM_TYPE_DESTINATION = 17;
    constexpr int TAG_ITEM_TYPE_SQUAWK_ERROR = 18;
    constexpr int TAG_ITEM_TYPE_INFO_STRING = 19;
    constexpr int TAG_ITEM_TYPE_TEMP_ALTITUDE = 20;
    constexpr int TAG_ITEM_TYPE_INFO_INDICATOR = 21;
    constexpr int TAG_ITEM_TYPE_FINAL_ALTITUDE = 22;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_SPEED = 23;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_RATE = 24;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_HEADING = 25;
    constexpr int TAG_ITEM_TYPE_SECTOR_INDICATOR = 26;
    constexpr int TAG_ITEM_TYPE_DUPLICATED_SQUAWK = 27;
    constexpr int TAG_ITEM_TYPE_COPN_COPX_NAME = 28;
    constexpr int TAG_ITEM_TYPE_COPN_COPX_ALTITUDE = 29;
    constexpr int TAG_ITEM_TYPE_FIR_COPX_NAME = 30;
    constexpr int TAG_ITEM_TYPE_COPX_NOT_CLEARED_ALTITUDE = 31;
    constexpr int TAG_ITEM_TYPE_COPX_AWERE_TEMP_ALTITUDE = 32;
    constexpr int TAG_ITEM_TYPE_NEXT_LINE_IF_NOT_EMPTY = 33;
    constexpr int TAG_ITEM_TYPE_DIRECT = 34;
    constexpr int TAG_ITEM_TYPE_GROUND_SPEED_OPTIONAL_WITH_N = 35;
    constexpr int TAG_ITEM_TYPE_FIR_COPX_NAME_OPTIONAL = 36;
    constexpr int TAG_ITEM_TYPE_DESTINATION_OPTIONAL = 37;
    constexpr int TAG_ITEM_TYPE_PLANE_TYPE_OPTIONAL = 38;
    constexpr int TAG_ITEM_TYPE_TSSR = 39;
    constexpr int TAG_ITEM_TYPE_GROUND_SPEED_WOUT_N = 40;
    constexpr int TAG_ITEM_TYPE_GROUND_SPEED_OPTIONAL_WOUT_N = 41;
    constexpr int TAG_ITEM_TYPE_COMPOUND_WARNING = 42;
    constexpr int TAG_ITEM_TYPE_TEMP_IFSET = 43;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_SPEED_IFSET = 44;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_RATE_IFSET = 45;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_HEADING_IFSET = 46;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_RUNWAY = 47;
    constexpr int TAG_ITEM_TYPE_COPN_NAME = 48;
    constexpr int TAG_ITEM_TYPE_COPN_ALTITUDE = 49;
    constexpr int TAG_ITEM_TYPE_COPN_TIME = 50;
    constexpr int TAG_ITEM_TYPE_COPX_NAME = 51;
    constexpr int TAG_ITEM_TYPE_COPX_ALTITUDE = 52;
    constexpr int TAG_ITEM_TYPE_COPX_TIME = 53;
    constexpr int TAG_ITEM_TYPE_ETA = 54;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_STAR = 55;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_SID = 56;
    constexpr int TAG_ITEM_TYPE_DEPARTURE_ORDER = 57;
    constexpr int TAG_ITEM_TYPE_CLEARENCE = 58;
    constexpr int TAG_ITEM_TYPE_GROUND_STATUS = 59;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_SQUAWK = 60;
    constexpr int TAG_ITEM_TYPE_ORIGIN = 61;
    constexpr int TAG_ITEM_TYPE_RVSM_FLAG = 62;
    constexpr int TAG_ITEM_TYPE_FLIGHT_RULE = 63;
    constexpr int TAG_ITEM_TYPE_SECTOR_INDICATOR_FIX = 64;
    constexpr int TAG_ITEM_TYPE_MANUAL_COORDINATION = 65;
    constexpr int TAG_ITEM_TYPE_INFO_ALWAYS = 66;
    constexpr int TAG_ITEM_TYPE_CLAM_WARNING = 67;
    constexpr int TAG_ITEM_TYPE_RAM_WARNING = 68;
    constexpr int TAG_ITEM_TYPE_SQ_OR_CALLSIGN = 69;
    constexpr int TAG_ITEM_TYPE_TWO_LETTER_GS = 70;
    constexpr int TAG_ITEM_TYPE_TWO_LETTER_GS_OPTIONAL = 71;
    constexpr int TAG_ITEM_TYPE_TWO_LETTER_ASSIGNED_SPEED = 72;
    constexpr int TAG_ITEM_TYPE_TWO_LETTER_ASSIGNED_SPEED_IFSET = 73;
    constexpr int TAG_ITEM_TYPE_NOT_REACHED_TEMPORARY = 74;
    constexpr int TAG_ITEM_TYPE_NOT_CLEARED_COPN_COPX_ALT = 75;
    constexpr int TAG_ITEM_TYPE_AIRCRAFT_CATEGORY_WITH_SLASH = 76;
    constexpr int TAG_ITEM_TYPE_NON_RVSM_FLAG = 77;
    constexpr int TAG_ITEM_TYPE_AC_TYPE_CATEGORY = 78;
    constexpr int TAG_ITEM_TYPE_AC_TYPE_CATEGORY_OPTIONAL = 79;
    constexpr int TAG_ITEM_TYPE_COMMUNICATION_TYPE_REDUCED = 80;
    constexpr int TAG_ITEM_TYPE_AIRLINE = 81;
    constexpr int TAG_ITEM_TYPE_FP_STATUS = 82;
    constexpr int TAG_ITEM_TYPE_ESTIMATE = 83;
    constexpr int TAG_ITEM_TYPE_ESTIMATE_ALWAYS = 84;
    constexpr int TAG_ITEM_TYPE_CONFLICTING_AC_CALLSING = 85;
    constexpr int TAG_ITEM_TYPE_CONFLICT_START = 86;
    constexpr int TAG_ITEM_TYPE_CONFLICT_END = 87;
    constexpr int TAG_ITEM_TYPE_CONFLICT_TYPE = 88;
    constexpr int TAG_ITEM_TYPE_MSAW_INDICATOR = 89;
    constexpr int TAG_ITEM_TYPE_SIMULATION_INDICATOR = 90;
    constexpr int TAG_ITEM_TYPE_SIMULATION_WAYPOINT = 91;
    constexpr int TAG_ITEM_TYPE_ASSIGNED_HEADING_STATIC = 92;
    constexpr int TAG_ITEM_TYPE_AIRLINE_NAME = 93;
    constexpr int TAG_ITEM_TYPE_SIMULATION_IAS = 94;
    constexpr int TAG_ITEM_TYPE_SIMULATION_ALTITUDE = 95;
    constexpr int TAG_ITEM_TYPE_SIMULATION_HEADING = 96;
    constexpr int TAG_ITEM_FUNCTION_NO = 0;
    constexpr int TAG_ITEM_FUNCTION_TOGGLE_ROUTE_DRAW = 1;
    constexpr int TAG_ITEM_FUNCTION_TOGGLE_ITEM_DISPLAY = 2;
    constexpr int TAG_ITEM_FUNCTION_TOGGLE_FIR_COPX_DISPLAY = 3;
    constexpr int TAG_ITEM_FUNCTION_TOGGLE_DEST_DISPLAY = 4;
    constexpr int TAG_ITEM_FUNCTION_TOGGLE_PLANE_TYPE_DISPLAY = 5;
    constexpr int TAG_ITEM_FUNCTION_TOGGLE_SI_STYLE = 6;
    constexpr int TAG_ITEM_FUNCTION_OPEN_FP_DIALOG = 7;
    constexpr int TAG_ITEM_FUNCTION_HANDOFF_POPUP_MENU = 8;
    constexpr int TAG_ITEM_FUNCTION_TAKE_HANDOFF = 9;
    constexpr int TAG_ITEM_FUNCTION_NEXT_ROUTE_POINTS_POPUP = 10;
    constexpr int TAG_ITEM_FUNCTION_TEMP_ALTITUDE_POPUP = 11;
    constexpr int TAG_ITEM_FUNCTION_ASSIGNED_SPEED_POPUP = 12;
    constexpr int TAG_ITEM_FUNCTION_ASSIGNED_RATE_POPUP = 13;
    constexpr int TAG_ITEM_FUNCTION_ASSIGNED_HEADING_POPUP = 14;
    constexpr int TAG_ITEM_FUNCTION_ASSIGNED_MACH_POPUP = 15;
    constexpr int TAG_ITEM_FUNCTION_TOGGLE_PREDICTION_DRAW = 16;
    constexpr int TAG_ITEM_FUNCTION_ASSIGNED_SID = 17;
    constexpr int TAG_ITEM_FUNCTION_ASSIGNED_STAR = 18;
    constexpr int TAG_ITEM_FUNCTION_ASSIGNED_RUNWAY = 19;
    constexpr int TAG_ITEM_FUNCTION_ASSIGNED_NEXT_CONTROLLER = 20;
    constexpr int TAG_ITEM_FUNCTION_COPN_NAME = 21;
    constexpr int TAG_ITEM_FUNCTION_COPX_NAME = 22;
    constexpr int TAG_ITEM_FUNCTION_COPN_ALTITUDE = 23;
    constexpr int TAG_ITEM_FUNCTION_COPX_ALTITUDE = 24;
    constexpr int TAG_ITEM_FUNCTION_ACCEPT_MANUAL_COORDINATION = 25;
    constexpr int TAG_ITEM_FUNCTION_COPN_COPX_ALTITUDE = 26;
    constexpr int TAG_ITEM_FUNCTION_SET_CLEARED_FLAG = 27;
    constexpr int TAG_ITEM_FUNCTION_SET_GROUND_STATUS = 28;
    constexpr int TAG_ITEM_FUNCTION_EDIT_SCRATCH_PAD = 29;
    constexpr int TAG_ITEM_FUNCTION_RFL_POPUP = 30;
    constexpr int TAG_ITEM_FUNCTION_SQUAWK_POPUP = 31;
    constexpr int TAG_ITEM_FUNCTION_COMMUNICATION_POPUP = 32;
    constexpr int TAG_ITEM_FUNCTION_CORRELATE_POPUP = 33;
    constexpr int TAG_ITEM_FUNCTION_SET_FP_STATUS = 34;
    constexpr int TAG_ITEM_FUNCTION_SET_ESTIMATE = 35;
    constexpr int TAG_ITEM_FUNCTION_SIMUL_TO_POPUP = 37;
    constexpr int TAG_ITEM_FUNCTION_SIMUL_LAND_VACATE_POPUP = 38;
    constexpr int TAG_ITEM_FUNCTION_SIMUL_TAXI_POPUP = 39;
    constexpr int TAG_ITEM_FUNCTION_SIMUL_TAXI_BEHIND = 40;
    constexpr int TAG_ITEM_FUNCTION_SIMULATION_POPUP = 41;
    constexpr int TAG_ITEM_FUNCTION_SIMUL_NEXT_WAYPOINTS = 42;
    constexpr int TAG_ITEM_FUNCTION_SIMUL_HOLDING_POINTS = 43;
    constexpr int TAG_ITEM_FUNCTION_CONFLICT_DETECTION_TOOL2 = 44;
    constexpr int TAG_ITEM_FUNCTION_SIMUL_ROUTES_POPUP = 45;
    constexpr int TAG_ITEM_FUNCTION_SET_GROUND_STATUS_ADVANCED = 46;
    constexpr int TAG_DATA_UNCORRELATED_RADAR = 1;
    constexpr int TAG_DATA_FLIGHT_PLAN_TRACK = 2;
    constexpr int TAG_DATA_CORRELATED = 3;
    constexpr int BUTTON_LEFT = 1;
    constexpr int BUTTON_MIDDLE = 2;
    constexpr int BUTTON_RIGHT = 3;
    constexpr int POPUP_ELEMENT_UNCHECKED = 0;
    constexpr int POPUP_ELEMENT_CHECKED = 1;
    constexpr int POPUP_ELEMENT_NO_CHECKBOX = 2;
    constexpr int CONNECTION_TYPE_NO = 0;
    constexpr int CONNECTION_TYPE_DIRECT = 1;
    constexpr int CONNECTION_TYPE_VIA_PROXY = 2;
    constexpr int CONNECTION_TYPE_SIMULATOR_SERVER = 3;
    constexpr int CONNECTION_TYPE_PLAYBACK = 4;
    constexpr int CONNECTION_TYPE_SIMULATOR_CLIENT = 5;
    constexpr int CONNECTION_TYPE_SWEATBOX = 6;
    constexpr int COORDINATION_STATE_NONE = 1;
    constexpr int COORDINATION_STATE_REQUESTED_BY_ME = 2;
    constexpr int COORDINATION_STATE_REQUESTED_BY_OTHER = 3;
    constexpr int COORDINATION_STATE_ACCEPTED = 4;
    constexpr int COORDINATION_STATE_REFUSED = 5;
    constexpr int COORDINATION_STATE_MANUAL_ACCEPTED = 6;
    constexpr int SECTOR_ELEMENT_INFO = 0;
    constexpr int SECTOR_ELEMENT_VOR = 1;
    constexpr int SECTOR_ELEMENT_NDB = 2;
    constexpr int SECTOR_ELEMENT_AIRPORT = 3;
    constexpr int SECTOR_ELEMENT_RUNWAY = 4;
    constexpr int SECTOR_ELEMENT_FIX = 5;
    constexpr int SECTOR_ELEMENT_STAR = 6;
    constexpr int SECTOR_ELEMENT_SID = 7;
    constexpr int SECTOR_ELEMENT_LOW_AIRWAY = 8;
    constexpr int SECTOR_ELEMENT_HIGH_AIRWAY = 9;
    constexpr int SECTOR_ELEMENT_HIGH_ARTC = 10;
    constexpr int SECTOR_ELEMENT_ARTC = 11;
    constexpr int SECTOR_ELEMENT_LOW_ARTC = 12;
    constexpr int SECTOR_ELEMENT_GEO = 13;
    constexpr int SECTOR_ELEMENT_FREE_TEXT = 14;
    constexpr int SECTOR_ELEMENT_AIRSPACE = 15;
    constexpr int SECTOR_ELEMENT_POSITION = 16;
    constexpr int SECTOR_ELEMENT_SIDS_STARS = 17;
    constexpr int SECTOR_ELEMENT_RADARS = 18;
    constexpr int SECTOR_ELEMENT_REGIONS = 19;
    constexpr int SECTOR_ELEMENT_NUMBER = 20;
    constexpr int SECTOR_ELEMENT_ALL = -1;
    constexpr int RADAR_POSITION_NONE = 0;
    constexpr int RADAR_POSITION_PRIMARY = 1;
    constexpr int RADAR_POSITION_SECONDARY_C = 2;
    constexpr int RADAR_POSITION_SECONDARY_S = 4;
    constexpr int RADAR_POSITION_ALL = 7;

    class NATC_ES_API CSectorElement;
    class NATC_ES_API CRadarTarget;
    class NATC_ES_API CPlugIn;

    class NATC_ES_API CPosition
    {
    public:
        double m_Latitude;
        double m_Longitude;

        inline CPosition(void)
        {
            m_Latitude = m_Longitude = 0.0;
        };

        bool LoadFromStrings(const char* sLongitude, const char* sLatitude);
        double DistanceTo(const CPosition OtherPosition) const;
        double DirectionTo(const CPosition OtherPosition) const;
    };

    class NATC_ES_API CFlightPlanExtractedRoute
    {
    private:
        void* m_FpPosition;
        friend class CFlightPlan;

    public:
        inline CFlightPlanExtractedRoute(void)
        {
            m_FpPosition = NULL;
        };

        int GetPointsNumber(void) const;
        int GetPointsCalculatedIndex(void) const;
        int GetPointsAssignedIndex(void) const;
        const char* GetPointName(int Index) const;
        CPosition GetPointPosition(int Index) const;
        const char* GetPointAirwayName(int Index) const;
        int GetPointAirwayClassification(int Index) const;
        int GetPointDistanceInMinutes(int Index) const;
        int GetPointCalculatedProfileAltitude(int Index) const;
    };

    class NATC_ES_API CFlightPlanPositionPredictions
    {
    private:
        void* m_FpPosition;
        friend class CFlightPlan;

    public:
        inline CFlightPlanPositionPredictions(void)
        {
            m_FpPosition = NULL;
        };

        int GetPointsNumber(void) const;
        CPosition GetPosition(int Index) const;
        int GetAltitude(int Index) const;
        const char* GetControllerId(int Index) const;
    };

    class NATC_ES_API CRadarTargetPositionData
    {
    private:
        void* m_RtPosition;
        void* m_PosPosition;
        friend class CRadarTarget;
        friend class CFlightPlan;

    public:
        inline CRadarTargetPositionData(void)
        {
            m_RtPosition = m_PosPosition = NULL;
        };

        inline bool IsValid(void) const
        {
            return m_RtPosition != NULL;
        };

        inline bool IsFPTrackPosition(void) const
        {
            return m_PosPosition == NULL;
        };

        int GetReceivedTime(void) const;
        CPosition GetPosition(void) const;
        const char* GetSquawk(void) const;
        bool GetTransponderC(void) const;
        bool GetTransponderI(void) const;
        int GetPressureAltitude(void) const;
        int GetFlightLevel(void) const;
        int GetReportedGS(void) const;
        int GetReportedHeading(void) const;
        int GetReportedHeadingTrueNorth(void) const;
        int GetReportedPitch(void) const;
        int GetReportedBank(void) const;
        int GetRadarFlags(void) const;
    };

    class NATC_ES_API CFlightPlanData
    {
    private:
        void* m_FpPosition;
        friend class CFlightPlan;

    public:
        inline CFlightPlanData(void)
        {
            m_FpPosition = NULL;
        };

        bool IsReceived(void) const;
        bool IsAmended(void) const;
        bool AmendFlightPlan(void);
        const char* GetPlanType(void) const;
        bool SetPlanType(const char* sPlanType);
        const char* GetAircraftInfo(void) const;
        bool SetAircraftInfo(const char* sInfo);
        char GetAircraftWtc(void) const;
        char GetAircraftType(void) const;
        int GetEngineNumber(void) const;
        char GetEngineType(void) const;
        char GetCapibilities(void) const;
        bool IsRvsm(void) const;
        const char* GetManufacturerType(void) const;
        const char* GetAircraftFPType(void) const;
        int GetTrueAirspeed(void) const;
        bool SetTrueAirspeed(int TrueAirspeed);
        const char* GetOrigin(void) const;
        bool SetOrigin(const char* sOrigin);
        int GetFinalAltitude(void) const;
        bool SetFinalAltitude(int FinalAltitude);
        const char* GetDestination(void) const;
        bool SetDestination(const char* sDestination);
        const char* GetAlternate(void) const;
        bool SetAlternate(const char* sAlternate);
        const char* GetRemarks(void) const;
        bool SetRemarks(const char* sRemarks);
        char GetCommunicationType(void) const;
        const char* GetRoute(void) const;
        bool SetRoute(const char* sRoute);
        const char* GetSidName(void) const;
        const char* GetStarName(void) const;
        const char* GetDepartureRwy(void) const;
        const char* GetArrivalRwy(void) const;
        const char* GetEstimatedDepartureTime(void) const;
        bool SetEstimatedDepartureTime(const char* sDepTime);
        const char* GetActualDepartureTime(void) const;
        bool SetActualDepartureTime(const char* sDepTime);
        const char* GetEnrouteHours(void) const;
        bool SetEnrouteHours(const char* sEnrouteHours);
        const char* GetEnrouteMinutes(void) const;
        bool SetEnrouteMinutes(const char* sEnrouteMinutes);
        const char* GetFuelHours(void) const;
        bool SetFuelHours(const char* sFuelHours);
        const char* GetFuelMinutes(void) const;
        bool SetFuelMinutes(const char* sFuelMinutes);
        int PerformanceGetIas(int Altitude, int VerticalSpeed);
        int PerformanceGetMach(int Altitude, int VerticalSpeed);
        int PerformanceGetClimbRate(int Altitude);
        int PerformanceGetDescentRate(int Altitude);
    };

    class NATC_ES_API CFlightPlanControllerAssignedData
    {
    private:
        void* m_FpPosition;
        friend class CFlightPlan;

    public:
        inline CFlightPlanControllerAssignedData(void)
        {
            m_FpPosition = NULL;
        };

        const char* GetSquawk(void) const;
        bool SetSquawk(const char* sSquawk);
        int GetFinalAltitude(void) const;
        bool SetFinalAltitude(int FinalAltitude);
        int GetClearedAltitude(void) const;
        bool SetClearedAltitude(int ClearedAltitude);
        char GetCommunicationType(void) const;
        bool SetCommunicationType(char CommunicationType);
        const char* GetScratchPadString(void) const;
        bool SetScratchPadString(const char* sString);
        int GetAssignedSpeed(void) const;
        bool SetAssignedSpeed(int AssignedSpeed);
        int GetAssignedMach(void) const;
        bool SetAssignedMach(int AssignedMach);
        int GetAssignedRate(void) const;
        bool SetAssignedRate(int AssignedRate);
        int GetAssignedHeading(void) const;
        bool SetAssignedHeading(int AssignedHeading);
        const char* GetDirectToPointName(void) const;
        bool SetDirectToPointName(const char* sPointName);
        const char* GetFlightStripAnnotation(int Index) const;
        bool SetFlightStripAnnotation(int Index, const char* sAnnotation);
    };

    class NATC_ES_API CFlightPlan
    {
    private:
        void* m_FpPosition;
        friend class ::CPlugInData;
        friend class CPlugIn;
        friend class CFlightPlanList;
        friend class CRadarTarget;

    public:
        inline CFlightPlan(void)
        {
            m_FpPosition = NULL;
        };

        inline bool IsValid(void) const
        {
            return m_FpPosition != NULL;
        };

        const char* GetCallsign(void) const;
        const char* GetPilotName(void) const;
        int GetState(void) const;
        int GetFPState(void) const;
        bool GetSimulated(void) const;
        const char* GetTrackingControllerCallsign(void) const;
        const char* GetTrackingControllerId(void) const;
        bool GetTrackingControllerIsMe(void) const;
        const char* GetHandoffTargetControllerCallsign(void) const;
        const char* GetHandoffTargetControllerId(void) const;
        double GetDistanceToDestination(void) const;
        double GetDistanceFromOrigin(void) const;
        const char* GetNextCopxPointName(void) const;
        const char* GetNextFirCopxPointName(void) const;
        int GetSectorEntryMinutes(void) const;
        int GetSectorExitMinutes(void) const;
        bool GetRAMFlag(void) const;
        bool GetCLAMFlag(void) const;
        const char* GetGroundState(void) const;
        bool GetClearenceFlag(void) const;
        bool IsTextCommunication(void) const;
        int GetFinalAltitude(void) const;
        int GetClearedAltitude(void) const;
        int GetEntryCoordinationPointState(void) const;
        const char* GetEntryCoordinationPointName(void) const;
        int GetEntryCoordinationAltitudeState(void) const;
        int GetEntryCoordinationAltitude(void) const;
        int GetExitCoordinationNameState(void) const;
        const char* GetExitCoordinationPointName(void) const;
        int GetExitCoordinationAltitudeState(void) const;
        int GetExitCoordinationAltitude(void) const;
        const char* GetCoordinatedNextController(void) const;
        int GetCoordinatedNextControllerState(void) const;
        CRadarTarget GetCorrelatedRadarTarget(void) const;
        bool CorrelateWithRadarTarget(CRadarTarget RadarTarget);
        void Uncorrelate(void);
        bool StartTracking(void);
        bool EndTracking(void);
        bool InitiateHandoff(const char* sTargetController);
        void AcceptHandoff(void);
        void RefuseHandoff(void);
        bool InitiateCoordination(const char* sTargetController, const char* sPointName, int Altitude);
        void AcceptCoordination(void);
        void RefuseCoordination(void);
        void PushFlightStrip(const char* sTargetController);
        void SetEstimation(const char* sPointName, const char* sTime);
        void ClearEstimation(void);
        void ClearEstimation(const char* sPointName);
        CFlightPlanExtractedRoute GetExtractedRoute(void) const;
        CFlightPlanPositionPredictions GetPositionPredictions(void) const;
        CRadarTargetPositionData GetFPTrackPosition(void) const;
        CFlightPlanData GetFlightPlanData(void) const;
        CFlightPlanControllerAssignedData GetControllerAssignedData(void) const;
    };

    class NATC_ES_API CRadarTarget
    {
    private:
        void* m_RtPosition;
        friend class ::CPlugInData;
        friend class CPlugIn;
        friend class CFlightPlan;

    public:
        inline CRadarTarget(void)
        {
            m_RtPosition = NULL;
        };

        inline bool IsValid(void) const
        {
            return m_RtPosition != NULL;
        };

        const char* GetCallsign(void) const;
        const char* GetSystemID(void) const;
        int GetVerticalSpeed(void) const;
        double GetTrackHeading(void) const;
        int GetGS(void) const;
        CFlightPlan GetCorrelatedFlightPlan(void) const;
        bool CorrelateWithFlightPlan(CFlightPlan FlightPlan);
        void Uncorrelate(void);
        CRadarTargetPositionData GetPosition(void) const;
        CRadarTargetPositionData GetPreviousPosition(const CRadarTargetPositionData CurrentPosition) const;
    };

    class NATC_ES_API CController
    {
    private:
        void* m_CtrPosition;
        bool m_Myself;
        friend class ::CPlugInData;
        friend class CPlugIn;

    public:
        inline CController(void)
        {
            m_CtrPosition = NULL;
            m_Myself = false;
        };

        inline bool IsValid(void) const
        {
            return m_CtrPosition != NULL || m_Myself;
        };

        const char* GetCallsign(void) const;
        const char* GetPositionId(void) const;
        bool GetPositionIdentified(void) const;
        double GetPrimaryFrequency(void) const;
        const char* GetFullName(void) const;
        int GetRating(void) const;
        int GetFacility(void) const;
        const char* GetSectorFileName(void) const;
        bool IsController(void) const;
        CPosition GetPosition(void) const;
        int GetRange(void) const;
        bool IsBreaking(void) const;
        bool IsOngoingAble(void) const;
    };

    class NATC_ES_API CRadarScreen
    {
    private:
        ::CRadarView* m_pRadarView;
        CPlugIn* m_pPlugIn;
        friend ::CPlugInData;

    public:
        CRadarScreen(void);

        inline CPlugIn* GetPlugIn(void)
        {
            return m_pPlugIn;
        };

        inline ::CRadarView* GetRadarView(void)
        {
            return m_pRadarView;
        };

        RECT GetToolbarArea(void);
        RECT GetRadarArea(void);
        RECT GetChatArea(void);
        CPosition ConvertCoordFromPixelToPosition(POINT Pt);
        POINT ConvertCoordFromPositionToPixel(CPosition Pos);
        void SaveDataToAsr(const char* sVariableName, const char* sVariableDescription, const char* sValue);
        const char* GetDataFromAsr(const char* sVariableName);
        void AddScreenObject(int ObjectType, const char* sObjectId, RECT Area, bool Moveable, const char* sMessage);
        void RequestRefresh(void);
        void ShowSectorFileElement(CSectorElement Element, const char* sComponentName, bool Show);
        void RefreshMapContent(void);
        void StartTagFunction(const char* sCallsign, const char* sItemPlugInName, int ItemCode, const char* sItemString,
                              const char* sFunctionPlugInName, int FunctionId, POINT Pt, RECT Area);
        void GetDisplayArea(CPosition* pLeftDown, CPosition* pRightUp);
        void SetDisplayArea(CPosition LeftDown, CPosition RightUp);

        // The virtual functions, in the order of the vtable.
        inline virtual void OnAsrContentLoaded(bool Loaded) {};
        virtual void OnAsrContentToBeSaved(void) {};
        inline virtual void OnRefresh(HDC hDC, int Phase) {};
        virtual void OnAsrContentToBeClosed(void) = 0;
        inline virtual void OnControllerPositionUpdate(CController Controller) {};
        inline virtual void OnControllerDisconnect(CController Controller) {};
        inline virtual void OnRadarTargetPositionUpdate(CRadarTarget RadarTarget) {};
        inline virtual void OnFlightPlanDisconnect(CFlightPlan FlightPlan) {};
        inline virtual void OnFlightPlanFlightPlanDataUpdate(CFlightPlan FlightPlan) {};
        inline virtual void OnFlightPlanControllerAssignedDataUpdate(CFlightPlan FlightPlan, int DataType) {};
        inline virtual void OnFlightPlanFlightStripPushed(CFlightPlan FlightPlan, const char* sSenderController,
                                                          const char* sTargetController) {};
        inline virtual bool OnCompileCommand(const char* sCommandLine) { return false; };
        inline virtual void OnOverScreenObject(int ObjectType, const char* sObjectId, POINT Pt, RECT Area) {};
        inline virtual void OnButtonDownScreenObject(int ObjectType, const char* sObjectId, POINT Pt, RECT Area, int Button) {};
        inline virtual void OnButtonUpScreenObject(int ObjectType, const char* sObjectId, POINT Pt, RECT Area, int Button) {};
        inline virtual void OnClickScreenObject(int ObjectType, const char* sObjectId, POINT Pt, RECT Area, int Button) {};
        inline virtual void OnDoubleClickScreenObject(int ObjectType, const char* sObjectId, POINT Pt, RECT Area, int Button) {};
        inline virtual void OnMoveScreenObject(int ObjectType, const char* sObjectId, POINT Pt, RECT Area, bool Released) {};
        inline virtual void OnFunctionCall(int FunctionId, const char* sItemString, POINT Pt, RECT Area) {};
    };

    class NATC_ES_API CFlightPlanList
    {
    private:
        void* m_Position;
        friend class ::CPlugInData;
        friend class CPlugIn;

    public:
        inline CFlightPlanList(void)
        {
            m_Position = NULL;
        };

        inline bool IsValid(void) const
        {
            return m_Position != NULL;
        };

        int GetColumnNumber(void);
        void DeleteAllColumns(void);
        void AddColumnDefinition(const char* sColumnTitle, int Width, bool Centered, const char* sItemProvifer, int ItemCode,
                                 const char* sLeftButtonFunctionProvifer, int LeftButtonFunction,
                                 const char* sRightButtonFunctionProvifer, int RightButtonFunction);
        void AddFpToTheList(CFlightPlan FlightPlan);
        void RemoveFpFromTheList(CFlightPlan FlightPlan);
        void ShowFpList(bool Show);
    };

    class NATC_ES_API CSectorElement
    {
    private:
        int m_Position;
        int m_ElementType;
        friend class ::CPlugInData;
        friend class CPlugIn;
        friend class CRadarScreen;

    public:
        inline CSectorElement(void)
        {
            m_Position = -1;
            m_ElementType = 0;
        };

        inline bool IsValid(void) const
        {
            return m_Position != -1;
        };

        inline int GetElementType(void) const
        {
            return m_ElementType;
        };

        const char* GetName(void) const;
        bool GetPosition(CPosition* pPosition, int Index);
        const char* GetComponentName(int Index);
        double GetFrequency(void) const;
        const char* GetRunwayName(int Index) const;
        int GetRunwayHeading(int Index) const;
        const char* GetAirportName(void) const;
        bool IsElementActive(bool Departure, int Index = 0);
    };

    class NATC_ES_API CGrountToAirChannel
    {
    private:
        int m_Index;
        friend class CPlugIn;
        friend class ::CPlugInData;

    public:
        inline CGrountToAirChannel(void)
        {
            m_Index = -1;
        }

        inline bool IsValid(void) const
        {
            return m_Index != -1;
        };

        const char* GetName(void);
        double GetFrequency(void);
        const char* GetVoiceServer(void);
        const char* GetVoiceChannel(void);
        bool GetIsPrimary(void);
        bool GetIsAtis(void);
        bool GetIsTextReceiveOn(void);
        bool GetIsTextTransmitOn(void);
        bool GetIsVoiceReceiveOn(void);
        bool GetIsVoiceTransmitOn(void);
        bool GetIsVoiceConnected(void);
        void TogglePrimary(void);
        void ToggleAtis(void);
        void ToggleTextReceive(void);
        void ToggleTextTransmit(void);
        void ToggleVoiceReceive(void);
        void ToggleVoiceTransmit(void);
    };

    class NATC_ES_API CPlugIn
    {
    private:
        ::CPlugInData* m_pPluginData;
#ifdef NATC_ES_BRIDGE
        friend class ::CPlugInData;  // the host only; friendship changes nothing in the object or its names
#endif

    public:
        CPlugIn(int CompatibilityCode, const char* sPlugInName, const char* sVersionNumber, const char* sAuthorName,
                const char* sCopyrigthMessage);
        virtual ~CPlugIn(void);

        // Virtual functions, in the order of the vtable (the voice events come last, after the other members).
        inline virtual void OnControllerPositionUpdate(CController Controller) {};
        inline virtual void OnControllerDisconnect(CController Controller) {};
        inline virtual void OnRadarTargetPositionUpdate(CRadarTarget RadarTarget) {};
        inline virtual void OnFlightPlanDisconnect(CFlightPlan FlightPlan) {};
        inline virtual void OnFlightPlanFlightPlanDataUpdate(CFlightPlan FlightPlan) {};
        inline virtual void OnPlaneInformationUpdate(const char* sCallsign, const char* sLivery, const char* sPlaneType) {};
        inline virtual void OnFlightPlanControllerAssignedDataUpdate(CFlightPlan FlightPlan, int DataType) {};
        inline virtual void OnFlightPlanFlightStripPushed(CFlightPlan FlightPlan, const char* sSenderController,
                                                          const char* sTargetController) {};
        inline virtual CRadarScreen* OnRadarScreenCreated(const char* sDisplayName, bool NeedRadarContent, bool GeoReferenced,
                                                          bool CanBeSaved, bool CanBeCreated) { return NULL; };
        inline virtual bool OnCompileCommand(const char* sCommandLine) { return false; };
        inline virtual void OnCompileFrequencyChat(const char* sSenderCallsign, double Frequency, const char* sChatMessage) {};
        inline virtual void OnCompilePrivateChat(const char* sSenderCallsign, const char* sReceiverCallsign,
                                                 const char* sChatMessage) {};
        inline virtual void OnGetTagItem(CFlightPlan FlightPlan, CRadarTarget RadarTarget, int ItemCode, int TagData,
                                         char sItemString[16], int* pColorCode, COLORREF* pRGB, double* pFontSize) {};
        inline virtual void OnRefreshFpListContent(CFlightPlanList AcList) {};
        inline virtual void OnNewMetarReceived(const char* sStation, const char* sFullMetar) {};
        inline virtual void OnFunctionCall(int FunctionId, const char* sItemString, POINT Pt, RECT Area) {};
        inline virtual void OnAirportRunwayActivityChanged(void) {};
        inline virtual void OnTimer(int Counter) {};

        const char* GetPlugInName(void);
        void RegisterDisplayType(const char* sDisplayName, bool NeedRadarContent, bool GeoReferenced, bool CanBeSaved,
                                 bool CanBeCreated);
        void RegisterTagItemType(const char* sDisplayName, int Code);
        void RegisterTagItemFunction(const char* sDisplayName, int Code);
        CFlightPlanList RegisterFpList(const char* sListName);
        void RegisterToolbarItem(int ItemId, const char* sItemName);
        void RefreshToolbar(bool ResizeToo);
        void SaveDataToSettings(const char* sVariableName, const char* sVariableDescription, const char* sValue);
        const char* GetDataFromSettings(const char* sVariableName);
        void OpenPopupEdit(RECT Area, int FunctionId, const char* sInitialValue);
        void OpenPopupList(RECT Area, const char* sTitle, int ColumnNumber);
        void AddPopupListElement(const char* sString1, const char* sString2, int FunctionId, bool Selected = false,
                                 int Checked = POPUP_ELEMENT_NO_CHECKBOX, bool Disabled = false, bool Fixed = false);
        int GetConnectionType(void) const;
        void SelectActiveSectorfile(void);
        void SelectScreenSectorfile(CRadarScreen* pRadarScreen);
        void SetASELAircraft(const CFlightPlan FlightPlan);
        void SetASELAircraft(const CRadarTarget RadarTarget);
        CFlightPlan FlightPlanSelect(const char* sCallsign) const;
        CRadarTarget RadarTargetSelect(const char* sCallsign) const;
        CFlightPlan FlightPlanSelectFirst(void) const;
        CRadarTarget RadarTargetSelectFirst(void) const;
        CFlightPlan FlightPlanSelectNext(CFlightPlan CurrentFlightPlan) const;
        CRadarTarget RadarTargetSelectNext(CRadarTarget CurrentRadartarget) const;
        CFlightPlan FlightPlanSelectASEL(void) const;
        CRadarTarget RadarTargetSelectASEL(void) const;
        CController ControllerSelect(const char* sCallsign) const;
        CController ControllerSelectByPositionId(const char* sPositionId) const;
        CController ControllerMyself(void) const;
        CController ControllerSelectFirst(void) const;
        CController ControllerSelectNext(CController CurrentController) const;
        CSectorElement SectorFileElementSelectFirst(int ElementType) const;
        CSectorElement SectorFileElementSelectNext(CSectorElement CurrentElement, int ElementType) const;
        void DisplayUserMessage(const char* sHandlerName, const char* sSenderName, const char* sMessage, bool ShowHandler,
                                bool ShowUnread, bool ShowUnreadEvenIfBusy, bool StartFlashing, bool NeedConfirmation);
        CGrountToAirChannel GroundToArChannelSelectFirst(void);
        CGrountToAirChannel GroundToArChannelSelectNext(CGrountToAirChannel CurrentChannel);
        void AddAlias(const char* sAliasName, const char* sAliasValue);

        inline virtual void OnVoiceTransmitStarted(bool OnPrimary) {};
        inline virtual void OnVoiceTransmitEnded(bool OnPrimary) {};
        inline virtual void OnVoiceReceiveStarted(CGrountToAirChannel Channel) {};
        inline virtual void OnVoiceReceiveEnded(CGrountToAirChannel Channel) {};

        int GetTransitionAltitude(void);
    };
}

// Every plugin DLL exports these two: the first returns the plugin object, the second is called on unload.
void __declspec(dllexport) EuroScopePlugInInit(EuroScopePlugIn::CPlugIn** ppPlugInInstance);
void __declspec(dllexport) EuroScopePlugInExit(void);
