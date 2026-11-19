#!/usr/bin/env bash
set -euo pipefail

readonly native_baseline_commit="67a3c4f22bd1b38ac499f9756902e04fa4ed8444"
readonly source_port_commit="8ed19081c972a80a8b6996ed817581bc59cbcb4b"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../.." && pwd -P)"
source_port="${1:-$repo_root/../MissionPlanner-Avalonia}"
output_dir="$repo_root/docs/migration"

if ! git -C "$repo_root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "Native Mission Planner repository not found: $repo_root" >&2
  exit 1
fi
if ! git -C "$source_port" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "Avalonia source repository not found: $source_port" >&2
  exit 1
fi
if ! git -C "$repo_root" cat-file -e "$native_baseline_commit^{tree}"; then
  echo "Native migration baseline is unavailable: $native_baseline_commit" >&2
  exit 1
fi
if ! git -C "$source_port" cat-file -e "$source_port_commit^{tree}"; then
  echo "Avalonia migration source is unavailable: $source_port_commit" >&2
  exit 1
fi

temporary_dir="$(mktemp -d)"
trap 'rm -rf -- "$temporary_dir"' EXIT

native_files="$temporary_dir/native-files"
port_files="$temporary_dir/port-files"
native_output="$temporary_dir/NATIVE_SURFACE.tsv"
import_output="$temporary_dir/PORT_SOURCE_IMPORT.tsv"

# Always inventory the two frozen migration trees. Reading the current index would make a
# successfully removed WinForms file disappear from the audit on the next regeneration.
LC_ALL=C git -C "$repo_root" ls-tree -r --name-only "$native_baseline_commit" |
  LC_ALL=C awk '($0 ~ /\.cs$/ || $0 ~ /\.resx$/) &&
      $0 !~ /^ExtLibs\// && $0 !~ /^MissionPlannerTests\//' |
  LC_ALL=C sort > "$native_files"
LC_ALL=C git -C "$source_port" ls-tree -r --name-only "$source_port_commit" |
  LC_ALL=C sort > "$port_files"

canonical_name() {
  local value="${1##*/}"
  value="${value,,}"
  value="${value%.designer.cs}"
  value="${value%.axaml.cs}"
  value="${value%.cs}"
  value="${value%viewmodel}"
  value="${value%window}"
  value="${value%view}"
  printf '%s' "$value"
}

declare -A port_candidates_by_name=()
while IFS= read -r source_path; do
  case "$source_path" in
    src/MissionPlannerAvalonia/*.cs|src/MissionPlannerAvalonia/*.axaml|src/MissionPlannerAvalonia/**/*.cs|src/MissionPlannerAvalonia/**/*.axaml)
      candidate="$(canonical_name "$source_path")"
      existing="${port_candidates_by_name[$candidate]-}"
      if [[ -n "$existing" ]]; then
        existing+=";"
      fi
      port_candidates_by_name["$candidate"]="$existing$source_path"
      ;;
  esac
done < "$port_files"

# Native WinForms setup/configuration classes were frequently renamed or consolidated while
# retaining their user workflow in the Avalonia port. Keep the mapping explicit so deleting the
# replaced C# files cannot erase the evidence from the frozen native-surface inventory.
declare -A native_replacement_by_logical_path=(
  ["Radio/ComPort"]="Services/MavlinkSerialControlPort.cs;ViewModels/Setup/SetupActionPages.cs"
  ["Radio/Models"]="Services/SikRadioSettingsService.cs;ViewModels/Setup/SetupActionPages.cs;GCSViews/Setup/SikRadioView.axaml"
  ["Radio/Sikradio"]="Services/SikRadioSettingsService.cs;Services/SikRadioFirmwareService.cs;Services/MavlinkSerialControlPort.cs;ViewModels/Setup/SetupActionPages.cs;GCSViews/Setup/SikRadioView.axaml"
  ["Radio/Sikradio.Designer"]="GCSViews/Setup/SikRadioView.axaml"
  ["Radio/XModem"]="Services/SikRadioFirmwareService.cs;GCSViews/Setup/SikRadioView.axaml;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/ComPort"]="Services/MavlinkSerialControlPort.cs;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/Common"]="ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/Config"]="GCSViews/Setup/SikRadioView.axaml;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/Config.Designer"]="GCSViews/Setup/SikRadioView.axaml"
  ["SikRadio/ISikRadioForm"]="GCSViews/Setup/SikRadioView.axaml;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/MAVLinkSerialPort"]="Services/MavlinkSerialControlPort.cs"
  ["SikRadio/MainV2"]="AppState.cs;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/Program"]="Program.cs;GCSViews/Setup/SikRadioView.axaml"
  ["SikRadio/Properties/AssemblyInfo"]="MissionPlanner.csproj;Services/AppVersion.cs"
  ["SikRadio/Properties/Resources"]="GCSViews/Setup/SikRadioView.axaml"
  ["SikRadio/RFD900"]="Services/SikRadioSettingsService.cs;Services/SikRadioFirmwareService.cs;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/RFDLib/Array"]="Services/SikRadioSettingsService.cs"
  ["SikRadio/RFDLib/Collections"]="Services/SikRadioSettingsService.cs"
  ["SikRadio/RFDLib/GUI/Settings"]="GCSViews/Setup/SikRadioView.axaml;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/RFDLib/IO/ATCommand"]="ViewModels/Setup/SetupActionPages.cs;Services/MavlinkSerialControlPort.cs"
  ["SikRadio/RFDLib/IO/SerialPort/SerialPort"]="Services/MavlinkSerialControlPort.cs;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/RFDLib/RFDLib"]="Services/SikRadioSettingsService.cs;Services/SikRadioFirmwareService.cs"
  ["SikRadio/RFDLib/Text"]="Services/SikRadioSettingsService.cs"
  ["SikRadio/Rssi"]="GCSViews/Setup/SikRadioView.axaml;GCSViews/Setup/SikRadioView.axaml.cs;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/Rssi.Designer"]="GCSViews/Setup/SikRadioView.axaml"
  ["SikRadio/Terminal"]="GCSViews/Setup/SikRadioView.axaml;ViewModels/Setup/SetupActionPages.cs"
  ["SikRadio/Terminal.Designer"]="GCSViews/Setup/SikRadioView.axaml"
  ["SikRadio/ThemeManager"]="Theme/MpTheme.axaml;Services/ThemeService.cs"
  ["Script"]="Services/PythonScriptHost.cs;GCSViews/FlightDataView.axaml;ViewModels/FlightDataViewModel.cs;ViewModels/MainWindowViewModel.cs;MissionPlannerTests/Avalonia/MissionPlanner.Tests/PythonScriptHostTests.cs"
  ["temp"]="Views/ActionPageView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDeveloperToolsViewModel.cs;docs/migration/Reference/TEMP_HANDLER_AUDIT.md"
  ["Utilities/BoardDetect"]="Services/LegacyFirmwareUploader.cs;GCSViews/Setup/InstallFirmwareView.axaml.cs;ViewModels/Setup/InstallFirmwareViewModel.cs"
  ["Utilities/Firmware"]="Services/LegacyFirmwareUploader.cs;ViewModels/Setup/InstallFirmwareViewModel.cs;ViewModels/GCSViews/ConfigurationView/ConfigFirmwareLegacyViewModel.cs"
  ["Utilities/httpserver"]="Services/LocalKmlServer.cs;GCSViews/FlightPlannerView.axaml;ViewModels/FlightPlannerViewModel.cs;ViewModels/GeoRefViewModel.cs;MissionPlannerTests/Avalonia/MissionPlanner.Tests/LocalKmlServerTests.cs"
  ["wix/Drivers"]="build/windows/msi/Package.wxs;build/windows/package.sh;README.md;docs/RELEASE.md"
  ["wix/Program"]="build/windows/msi/Package.wxs;build/windows/msi/MissionPlanner.Installer.wixproj;build/windows/package.sh;build/version.sh;.github/workflows/ci.yml;.github/workflows/release.yml"
  ["wix/Properties/AssemblyInfo"]="MissionPlanner.csproj;build/version.sh"
  ["test/FirmwareSelection.xaml"]="GCSViews/ConfigurationView/ConfigFirmwareLegacyView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFirmwareLegacyViewModel.cs"
  ["Plugin/Plugin"]="Plugin/LegacyCompatibility/PluginCompatibility.cs;Plugin/PortableApi/Plugin.cs;Plugin/PortableApi/PluginHost.cs"
  ["Plugin/PluginLoader"]="Services/PluginRuntime.cs;Services/PluginService.cs"
  ["Plugin/PluginUI"]="Views/PluginManagerWindow.axaml;ViewModels/PluginManagerViewModel.cs"
  ["plugins/Dowding/DowdingPlugin"]="docs/migration/Reference/DOWDING_AUDIT.md;Controls/MapView.cs;Services/PluginRuntime.cs;Services/PluginService.cs"
  ["plugins/Dowding/DowdingUI"]="docs/migration/Reference/DOWDING_AUDIT.md;Views/AntennaTrackerUIView.axaml;ViewModels/AntennaTrackerUIViewModel.cs;Views/SerialOutputCotView.axaml;ViewModels/SerialOutputCotViewModel.cs"
  ["plugins/Dowding/DowdingUI.Designer"]="docs/migration/Reference/DOWDING_AUDIT.md;Views/AntennaTrackerUIView.axaml;Views/SerialOutputCotView.axaml"
  ["Plugins/AnonymizeBinlogPlugin"]="Services/DataFlashLogAnonymizer.cs;ViewModels/GCSViews/ConfigurationView/ConfigAdvancedViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigAC_Fence"]="GCSViews/ConfigurationView/ConfigAC_FenceView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAC_FenceViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigADSB"]="GCSViews/ConfigurationView/ConfigADSBView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigADSBViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigAccelerometerCalibration"]="GCSViews/ConfigurationView/ConfigAccelCalibrationView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCalibrationPages.cs"
  ["GCSViews/ConfigurationView/ConfigAdvanced"]="Views/ActionPageView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAdvancedViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigAntennaTracker"]="GCSViews/ConfigurationView/ConfigAntennaTrackerParamView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAntennaTrackerParamViewModel.cs;GCSViews/ConfigurationView/ConfigAntennaTrackerView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAntennaTrackerViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigArducopter"]="GCSViews/ConfigurationView/ConfigBasicTuningView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigBasicTuningViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigArduplane"]="GCSViews/ConfigurationView/ConfigArduplaneView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigArduplaneViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigArdurover"]="GCSViews/ConfigurationView/ConfigArduroverView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigArduroverViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigBatteryMonitoring"]="GCSViews/ConfigurationView/ConfigBatteryMonitoringView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigBatteryMonitoringViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigBatteryMonitoring2"]="GCSViews/ConfigurationView/ConfigBatteryMonitoring2View.axaml;ViewModels/GCSViews/ConfigurationView/ConfigBatteryMonitoring2ViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigCompassMot"]="GCSViews/ConfigurationView/ConfigCompassMotView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCompassMotViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigCubeID"]="GCSViews/ConfigurationView/ConfigCubeIDView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCubeIDViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigDroneCAN"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigESCCalibration"]="GCSViews/ConfigurationView/ConfigESCCalibrationView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCalibrationPages.cs"
  ["GCSViews/ConfigurationView/ConfigFFT"]="GCSViews/ConfigurationView/ConfigFFTView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFFTViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFailSafe"]="GCSViews/ConfigurationView/ConfigFailSafeView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFailSafeViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFirmware"]="GCSViews/Setup/InstallFirmwareView.axaml;ViewModels/Setup/InstallFirmwareViewModel.cs;GCSViews/ConfigurationView/ConfigFirmwareLegacyView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFirmwareLegacyViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFirmwareManifest"]="GCSViews/Setup/InstallFirmwareView.axaml;ViewModels/Setup/InstallFirmwareViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFlightModes"]="GCSViews/ConfigurationView/ConfigFlightModesView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFlightModesViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFrameClassType"]="GCSViews/ConfigurationView/ConfigFrameClassTypeView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFrameClassTypeViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFrameType"]="GCSViews/ConfigurationView/ConfigFrameTypeView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFrameTypeViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFriendlyParams"]="GCSViews/ConfigurationView/ConfigFriendlyParamsView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFriendlyParamsViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigFriendlyParamsAdv"]="GCSViews/ConfigurationView/ConfigFriendlyParamsView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigFriendlyParamsViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigGPSOrder"]="GCSViews/ConfigurationView/ConfigGPSOrderView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigGPSOrderViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWAirspeed"]="GCSViews/ConfigurationView/ConfigAirspeedView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigAirspeedViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWBT"]="GCSViews/ConfigurationView/ConfigHWBTView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWBTViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWCAN"]="GCSViews/ConfigurationView/ConfigHWCANView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWCANViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWCompass"]="GCSViews/ConfigurationView/ConfigCompassLegacyView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCompassLegacyViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWCompass2"]="GCSViews/ConfigurationView/ConfigCompassView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCalibrationPages.cs"
  ["GCSViews/ConfigurationView/ConfigHWIDs"]="GCSViews/ConfigurationView/ConfigHWIDView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWIDViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWOSD"]="GCSViews/ConfigurationView/ConfigHWOSDView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWOSDViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWOptFlow"]="GCSViews/ConfigurationView/ConfigOptFlowView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigOptFlowViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWPX4Flow"]="GCSViews/ConfigurationView/ConfigPX4FlowView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigPX4FlowViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWParachute"]="GCSViews/ConfigurationView/ConfigParachuteView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigParachuteViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWRangeFinder"]="GCSViews/ConfigurationView/ConfigRangeFinderView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigRangeFinderViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigHWesp8266"]="GCSViews/ConfigurationView/ConfigHWESP8266View.axaml;ViewModels/GCSViews/ConfigurationView/ConfigHWESP8266ViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigInitialParams"]="GCSViews/ConfigurationView/ConfigInitialParamsView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigInitialParamsViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigMandatory"]="GCSViews/SetupView.axaml;ViewModels/SetupViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigMotorTest"]="GCSViews/ConfigurationView/ConfigMotorTestView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigCalibrationPages.cs"
  ["GCSViews/ConfigurationView/ConfigMount"]="GCSViews/ConfigurationView/ConfigMountView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigMountViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigOSD"]="GCSViews/ConfigurationView/ConfigOSDView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigOSDViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigOptional"]="GCSViews/SetupView.axaml;ViewModels/SetupViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigParamLoading"]="GCSViews/ConfigurationView/ConfigParamLoadingView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigParamLoadingViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigPlanner"]="GCSViews/ConfigurationView/ConfigPlannerView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigPlannerViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigPlannerAdv"]="GCSViews/ConfigurationView/ConfigPlannerAdvView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigPlannerAdvViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigREPL"]="GCSViews/ConfigurationView/ConfigOnboardReplView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigOnboardReplViewModel.cs;GCSViews/ConfigurationView/ConfigScriptReplView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigScriptReplViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigRadioInput"]="GCSViews/ConfigurationView/ConfigRadioInputView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigRadioInputViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigRadioOutput"]="GCSViews/ConfigurationView/ConfigRadioOutputView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigRadioOutputViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigRawParams"]="Views/RawParamsView.axaml;ViewModels/RawParamsViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSecure"]="GCSViews/ConfigurationView/ConfigSecureView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigSecureViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSecureAP"]="GCSViews/ConfigurationView/ConfigSecureApView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigSecureApViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSerial"]="GCSViews/ConfigurationView/ConfigSerialView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigSerialViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSerialInjectGPS"]="GCSViews/ConfigurationView/ConfigGpsInjectView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigGpsInjectViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigSimplePids"]="GCSViews/ConfigurationView/ConfigBasicTuningView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigBasicTuningViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigTerminal"]="GCSViews/ConfigurationView/ConfigTerminalView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigTerminalViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigTradHeli"]="GCSViews/ConfigurationView/ConfigTradHeliView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigTradHeliViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigTradHeli4"]="GCSViews/ConfigurationView/ConfigTradHeli4View.axaml;ViewModels/GCSViews/ConfigurationView/ConfigTradHeli4ViewModel.cs"
  ["GCSViews/ConfigurationView/ConfigUserDefined"]="GCSViews/ConfigurationView/ConfigUserDefinedView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigUserDefinedViewModel.cs"
  ["GCSViews/ConfigurationView/DeviceInfo"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["GCSViews/ConfigurationView/DroneCANModel"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["GCSViews/ConfigurationView/uitype"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["GCSViews/Help"]="Views/HelpView.axaml;ViewModels/HelpViewModel.cs"
  ["GCSViews/InitialSetup"]="GCSViews/SetupView.axaml;ViewModels/SetupViewModel.cs"
  ["GCSViews/SITL"]="Views/SimulationView.axaml;ViewModels/SimulationViewModel.cs;Services/SitlLauncher.cs"
  ["GCSViews/SoftwareConfig"]="Views/ConfigView.axaml;ViewModels/ConfigViewModel.cs"
  ["Controls/AuthKeys"]="ViewModels/GCSViews/ConfigurationView/ConfigAdvancedViewModel.cs"
  ["Controls/AuxOptions"]="GCSViews/FlightDataView.axaml;ViewModels/FlightDataViewModel.cs"
  ["Controls/ConnectionControl"]="Views/MainWindow.axaml;ViewModels/ConnectionViewModel.cs"
  ["Controls/ConnectionOptions"]="Views/ConnectionOptionsWindow.axaml;ViewModels/ConnectionOptionsViewModel.cs"
  ["Controls/ConnectionStats"]="Views/LinkStatsWindow.axaml;ViewModels/LinkStatsViewModel.cs"
  ["Controls/ControlSensorsStatus"]="Controls/HudControl.cs;Views/FlightDataDialogs.cs;ViewModels/FlightDataViewModel.cs"
  ["Controls/DefaultSettings"]="GCSViews/ConfigurationView/ConfigDefaultSettingsView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDefaultSettingsViewModel.cs"
  ["Controls/DevopsUI"]="Views/DeviceOperationsView.axaml;ViewModels/DeviceOperationsViewModel.cs"
  ["Controls/DistanceBar"]="GCSViews/FlightDataView.axaml;ViewModels/FlightDataViewModel.cs"
  ["Controls/DroneCANParams"]="GCSViews/ConfigurationView/ConfigDroneCanView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigDroneCanViewModel.cs"
  ["Controls/DroneCANInspector"]="Views/DroneCANInspectorView.axaml;ViewModels/DroneCANInspectorViewModel.cs;Views/DroneCanFieldGraphWindow.cs;Views/DroneCanSubscriberWindow.cs"
  ["Controls/DroneCANSubscriber"]="Views/DroneCanSubscriberWindow.cs;ViewModels/DroneCANInspectorViewModel.cs"
  ["Controls/EKFStatus"]="Views/FlightDataDialogs.cs;Controls/HudControl.cs"
  ["Controls/ElevationProfile"]="Views/ElevationGraphWindow.cs;ViewModels/FlightPlannerViewModel.cs"
  ["Controls/FollowMe"]="Views/FollowMeView.axaml;ViewModels/FollowMeViewModel.cs"
  ["Controls/GMAPCache"]="Views/MapCacheView.axaml;ViewModels/MapCacheViewModel.cs;Services/MapCacheManager.cs"
  ["Controls/GimbalControlSettingsForm"]="Controls/GimbalVideoOverlay.cs;Services/GimbalVideoInteraction.cs"
  ["Controls/GimbalVideoControl"]="Controls/VideoControl.cs;Controls/GimbalVideoOverlay.cs;Services/GimbalVideoInteraction.cs"
  ["Controls/Loading"]="Services/ProgressDialogs.cs"
  ["Controls/LogAnalyzer"]="Services/LogAnalyzer.cs;Views/LogMetadataWindow.cs"
  ["Controls/MAVLinkInspector"]="Views/MAVLinkInspectorView.axaml;ViewModels/MAVLinkInspectorViewModel.cs"
  ["Controls/MAVLinkParamChanged"]="ViewModels/GCSViews/ConfigurationView/ParamField.cs;ViewModels/GCSViews/ConfigurationView/ParamPageBase.cs"
  ["Controls/MavCommandSelection"]="GCSViews/ConfigurationView/ConfigMavCommandView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigMavCommandViewModel.cs"
  ["Controls/MavFTPUI"]="GCSViews/ConfigurationView/MavFTPUIView.axaml;ViewModels/GCSViews/ConfigurationView/MavFTPUIViewModel.cs"
  ["Controls/MavlinkCheckBox"]="Controls/FormField.cs;ViewModels/GCSViews/ConfigurationView/ParamField.cs"
  ["Controls/MavlinkCheckBoxBitMask"]="Controls/FormField.cs;ViewModels/GCSViews/ConfigurationView/ParamField.cs"
  ["Controls/MavlinkComboBox"]="Controls/FormField.cs;ViewModels/GCSViews/ConfigurationView/ParamField.cs"
  ["Controls/MavlinkNumericUpDown"]="Controls/FormField.cs;ViewModels/GCSViews/ConfigurationView/ParamField.cs"
  ["Controls/ModifyandSet"]="Views/ParamCompareWindow.cs;ViewModels/RawParamsViewModel.cs"
  ["Controls/MovingBase"]="Views/MovingBaseView.axaml;ViewModels/MovingBaseViewModel.cs"
  ["Controls/MyDataGridView"]="Views/RawParamsView.axaml;GCSViews/FlightPlannerView.axaml"
  ["Controls/OSDVideo"]="Views/OsdVideoOverlayWindow.axaml;ViewModels/OsdVideoOverlayViewModel.cs;Services/OsdVideoOverlay.cs"
  ["Controls/OpenGLtest2"]="Views/Terrain3DView.axaml;ViewModels/Terrain3DViewModel.cs;Services/Terrain3D.cs"
  ["Controls/PrearmStatus"]="Views/FlightDataDialogs.cs;Controls/HudControl.cs"
  ["Controls/PropagationSettings"]="Views/PropagationSettingsWindow.cs;Services/PropagationService.cs"
  ["Controls/ProximityControl"]="Views/ProximityWindow.cs;Controls/ProximityRadarControl.cs"
  ["Controls/RAW_Sensor"]="Views/FlightDataDialogs.cs;GCSViews/FlightDataView.axaml"
  ["Controls/RelayOptions"]="GCSViews/FlightDataView.axaml;ViewModels/FlightDataViewModel.cs"
  ["Controls/ScriptConsole"]="GCSViews/ConfigurationView/ConfigScriptReplView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigScriptReplViewModel.cs;Services/LuaScriptHost.cs"
  ["Controls/SB"]="Services/CubeServiceBulletin.cs;ViewModels/ConnectionViewModel.cs"
  ["Controls/SerialOutputCoT"]="Views/SerialOutputCotView.axaml;ViewModels/SerialOutputCotViewModel.cs"
  ["Controls/SerialOutputMD"]="Views/MicrodroneDownlinkView.axaml;ViewModels/MicrodroneDownlinkViewModel.cs;Services/MicrodroneDownlinkEncoder.cs"
  ["Controls/SerialOutputNMEA"]="Views/SerialOutputNMEAView.axaml;ViewModels/SerialOutputNMEAViewModel.cs"
  ["Controls/SerialOutputPass"]="Views/SerialPassThroughView.axaml;ViewModels/SerialPassThroughViewModel.cs"
  ["Controls/ServoOptions"]="GCSViews/FlightDataView.axaml;ViewModels/FlightDataViewModel.cs"
  ["Controls/SpectrogramUI"]="Views/SpectrogramWindow.cs"
  ["Controls/Status"]="GCSViews/FlightDataView.axaml;Views/FlightDataDialogs.cs"
  ["Controls/ThemeEditor"]="Views/ThemeEditorWindow.cs;Services/ThemeService.cs"
  ["Controls/ToolStripConnectionControl"]="Views/MainWindow.axaml;ViewModels/ConnectionViewModel.cs"
  ["Controls/Vibration"]="Views/FlightDataDialogs.cs;Controls/HudControl.cs"
  ["Controls/Video"]="Controls/VideoControl.cs;Services/VideoSourceResolver.cs;Services/MavlinkVideoStreams.cs"
  ["Controls/VideoStreamSelector"]="Controls/VideoControl.cs;Services/VideoSourceResolver.cs;Services/MavlinkVideoStreams.cs"
  ["Controls/fftui"]="Views/ConfigFFTWindow.cs;ViewModels/GCSViews/ConfigurationView/ConfigFFTViewModel.cs"
  ["Controls/paramcompare"]="Views/ParamCompareWindow.cs;ViewModels/RawParamsViewModel.cs"
  ["Utilities/GStreamerUI"]="Controls/VideoControl.cs;Services/VideoSourceResolver.cs;Services/MavlinkVideoStreams.cs"
  ["Utilities/LangUtility"]="Services/LocalizationService.cs;Services/ResxTranslationService.cs;Views/TranslationEditorWindow.axaml;ViewModels/TranslationEditorViewModel.cs"
  ["Utilities/LogAnalyzer"]="Services/LogAnalyzer.cs;Views/LogBrowseView.axaml;ViewModels/FlightDataViewModel.cs"
  ["Utilities/OsdTuningSlotProvider"]="Services/OsdTuningSlotService.cs;Views/OsdTuningSlotsWindow.cs;GCSViews/ConfigurationView/ConfigOSDView.axaml"
  ["Utilities/POI"]="Services/PoiStore.cs;Controls/MapView.cs;ViewModels/FlightPlannerViewModel.cs;ViewModels/FlightDataViewModel.cs"
  ["Utilities/SSHTerminal"]="Services/SshTerminalSession.cs;GCSViews/ConfigurationView/ConfigTerminalView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigTerminalViewModel.cs"
  ["Utilities/Speech"]="Services/Speech.cs;Services/SpeechAnnouncer.cs"
  ["Utilities/ThemeManager"]="Services/ThemeService.cs;Views/ThemeEditorWindow.cs;Theme/MpTheme.axaml"
  ["Utilities/Update"]="Services/Updater.cs;Services/AppVersion.cs;Views/HelpView.axaml"
  ["Utilities/Win32DeviceMgnt"]="ViewModels/Setup/InstallFirmwareViewModel.cs;ViewModels/GCSViews/ConfigurationView/ConfigFirmwareLegacyViewModel.cs"
  ["Utilities/XMLColor"]="Services/ThemeService.cs;Views/ThemeEditorWindow.cs"
  ["L10N"]="Services/LocalizationService.cs;Services/ResxTranslationService.cs"
  ["Common"]="Controls/MapView.cs;Controls/MavMarker.cs;Services/Dialogs.cs;Services/ProgressDialogs.cs"
  ["NativeMethods"]="Services/SystemAwakeService.cs;Services/LibVlcBootstrap.cs;Services/NativeGdalApi.cs;Services/MavLinkConnectionManager.cs"
  ["NoFly/NoFly"]="Services/NoFlyOverlay.cs;Services/NoFlyOverlayCoordinator.cs;Services/HongKongNoFlyService.cs"
  ["ResEdit"]="Views/TranslationEditorWindow.axaml;ViewModels/TranslationEditorViewModel.cs;Services/ResxTranslationService.cs"
  ["Warnings/WarningControl"]="GCSViews/ConfigurationView/WarningManagerView.axaml;ViewModels/GCSViews/ConfigurationView/WarningManagerViewModel.cs"
  ["Warnings/WarningsManager"]="GCSViews/ConfigurationView/WarningManagerView.axaml;ViewModels/GCSViews/ConfigurationView/WarningManagerViewModel.cs;ExtLibs/Utilities/Warnings/WarningEngine.cs"
  ["Updater/Program"]="Services/Updater.cs;build/make-update-bundle.py;build/update-public-key.txt"
  ["Updater/Properties/AssemblyInfo"]="MissionPlanner.csproj;Services/AppVersion.cs"
  ["Updater/Properties/Resources"]="Services/Updater.cs"
  ["resedit/Form1"]="Views/TranslationEditorWindow.axaml;ViewModels/TranslationEditorViewModel.cs;Services/ResxTranslationService.cs"
  ["resedit/Program"]="Views/TranslationEditorWindow.axaml;ViewModels/TranslationEditorViewModel.cs"
  ["resedit/Properties/AssemblyInfo"]="MissionPlanner.csproj"
  ["resedit/Properties/Resources"]="Services/ResxTranslationService.cs"
  ["resedit/Properties/Settings"]="ViewModels/TranslationEditorViewModel.cs"
  ["Splash"]="Views/MainWindow.axaml;Views/HelpView.axaml;Services/AppVersion.cs"
)

declare -A native_removal_by_logical_path=(
  ["GCSViews/ConfigurationView/ConfigAteryx"]="Ateryx-specific setup is retired with the legacy Ateryx HIL path; PORT_STATUS records SITL as its supported replacement."
  ["GCSViews/ConfigurationView/ConfigAteryxSensors"]="Ateryx-specific sensor setup is retired with the legacy Ateryx HIL path; PORT_STATUS records SITL as its supported replacement."
  ["GCSViews/ConfigurationView/ConfigFirmwareDisabled"]="The Avalonia setup navigation hides unavailable firmware actions instead of opening a WinForms disabled-placeholder page."
  ["Controls/DigitalSkyUI"]="DigitalSky is a retired third-party service integration and is intentionally not shipped without a current API/authentication/privacy review."
  ["Controls/DroneCANFileUI"]="The pinned native DroneCAN file browser is unreachable and half-stubbed; node parameters and firmware upload remain available in the native Avalonia DroneCAN page."
  ["Controls/OpenGLtest"]="The obsolete first OpenGL test surface is superseded by the tested native Avalonia Terrain 3D workflow."
  ["Controls/SerialSupportProxy"]="Support Proxy is intentionally disabled until authentication, explicit consent and a reviewed network design exist."
  ["Utilities/AirMarket"]="AirMarket is an unreachable legacy third-party upload integration that stores a password-derived credential and automatically sends flight logs; it is intentionally retired pending a current API, authentication, consent and privacy review."
  ["Utilities/CircleSurveyMission"]="The unreferenced beta circle helper is superseded by the native Avalonia planner mission builders and was never reachable from the pinned official UI."
  ["Utilities/ExtensionsMP"]="These are WinForms/Xamarin host, binding and DataGridView glue extensions; native Avalonia windows, bindings and lifecycle handling replace them directly."
  ["Utilities/ImageMatch"]="This unreferenced Accord imaging experiment uses hard-coded developer Windows paths and is not a shipped Mission Planner workflow."
  ["Utilities/NativeLibrary"]="The unused hand-written kernel32/libdl loader is superseded by System.Runtime.InteropServices.NativeLibrary in the portable native-library bootstrap services."
  ["GlobalSuppressions"]="The pinned file contains comments only and defines no suppression attributes. The Avalonia application builds with analyzers enabled and warnings treated as errors."
  ["Properties/Resources"]="The generated WinForms resource accessor is not compiled by the Avalonia application; the neutral and localized RESX files remain preserved for the translation workflow."
  ["ZZZLibShims"]="This file contains no-op compatibility stubs for the abandoned netstandard WinForms experiment. The Avalonia application uses real portable services and referenced libraries instead of these fake implementations."
)

find_candidates() {
  local wanted
  wanted="$(canonical_name "$1")"
  printf '%s' "${port_candidates_by_name[$wanted]-}"
}

printf 'native_path\tkind\tstatus\tported_candidates\tevidence_or_next_action\n' > "$native_output"
while IFS= read -r native_path; do
  kind="csharp"
  status="unported-blocker"
  candidates=""
  evidence="Requires code-level classification before project exclusion or deletion."

  case "$native_path" in
    *.resx)
      kind="resx"
      status="retain"
      evidence="Preserve neutral/culture resource until AXAML localization mapping is verified."
      ;;
    Radio/Uploader.cs|Radio/IHex.cs|Grid/GridData.cs)
      status="retain"
      candidates="$native_path"
      evidence="The tested port compiled this exact native source through a temporary compatibility copy; compile it directly in-place."
      ;;
    Properties/AssemblyInfo.cs)
      status="merge"
      candidates="MissionPlanner.csproj;Services/AppVersion.cs"
      evidence="Keep the upstream version and add the tracked local build plus canonical commit hash."
      ;;
    Program.cs)
      status="replace"
      candidates="Program.cs"
      evidence="Replace WinForms startup with the tested Avalonia entry point."
      ;;
    MainV2.cs|MainV2.Designer.cs)
      status="replace"
      candidates="Views/MainWindow.axaml;Views/MainWindow.axaml.cs;ViewModels/MainWindowViewModel.cs"
      evidence="Main application shell replacement; legacy plugin ABI must be merged into the main assembly first."
      ;;
    GCSViews/FlightData.cs|GCSViews/FlightData.Designer.cs)
      status="replace"
      candidates="GCSViews/FlightDataView.axaml;GCSViews/FlightDataView.axaml.cs;ViewModels/FlightDataViewModel.cs"
      evidence="Flight Data Avalonia replacement with splitter/session/airport fixes."
      ;;
    GCSViews/FlightPlanner.cs|GCSViews/FlightPlanner.Designer.cs)
      status="replace"
      candidates="GCSViews/FlightPlannerView.axaml;GCSViews/FlightPlannerView.axaml.cs;ViewModels/FlightPlannerViewModel.cs"
      evidence="Flight Planner Avalonia replacement."
      ;;
    *)
      logical_path="$native_path"
      logical_path="${logical_path%.Designer.cs}"
      logical_path="${logical_path%.designer.cs}"
      logical_path="${logical_path%.cs}"
      if [[ -v 'native_replacement_by_logical_path[$logical_path]' ]]; then
        status="replace"
        candidates="${native_replacement_by_logical_path[$logical_path]}"
        evidence="Mapped to the listed native Avalonia artifacts; feature-level parity and deliberate safety differences are recorded in docs/migration/Reference/PORT_STATUS.md."
      elif [[ -v 'native_removal_by_logical_path[$logical_path]' ]]; then
        status="remove"
        candidates="docs/migration/Reference/PORT_STATUS.md"
        evidence="${native_removal_by_logical_path[$logical_path]}"
      elif [[ "$logical_path" == Plugins/OpenDroneID2/* ]]; then
        status="replace"
        candidates="Views/OpenDroneIdView.axaml;ViewModels/OpenDroneIdViewModel.cs;Services/OpenDroneIdMessageFactory.cs;Services/NmeaGgaParser.cs"
        evidence="The native Avalonia Open Drone ID workflow ports identity/configuration, serial and network NMEA input, map status, emergency state and target-bound MAVLink transmission with lifecycle tests."
      elif [[ "$logical_path" == Plugins/TerrainMakerPlugin/* ]]; then
        status="replace"
        candidates="Views/TerrainMakerWindow.axaml;ViewModels/TerrainMakerViewModel.cs;Services/TerrainDataService.cs"
        evidence="The native Avalonia Terrain Maker ports the official DAT geometry, elevation priority and binary format with cancellable atomic output and regression tests."
      elif [[ "$logical_path" == plugins/FaceMap/* ]]; then
        status="replace"
        candidates="Views/FaceMapView.axaml;ViewModels/FaceMapViewModel.cs;ViewModels/FaceMapMissionBuilder.cs;Services/FaceMapSupport.cs"
        evidence="The native Avalonia FaceMap workflow ports geometry, preview, mission generation, split flights, camera triggers and persisted file compatibility with regression tests."
      elif [[ "$logical_path" == plugins/Shortcuts/Plugin ]]; then
        status="replace"
        candidates="Services/FlightCommandShortcuts.cs;GCSViews/ConfigurationView/ConfigPlannerView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigPlannerViewModel.cs"
        evidence="Every active official Shortcuts command is exposed through opt-in, target-bound Avalonia shortcuts with safety confirmations and regression tests."
      elif [[ "$logical_path" == plugins/InitialParamsCalculator ]]; then
        status="replace"
        candidates="GCSViews/ConfigurationView/ConfigInitialParamsView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigInitialParamsViewModel.cs"
        evidence="The legacy loose-source calculator is superseded by the newer native Initial Parameters page with reviewable, selected existing-parameter writes."
      elif [[ "$logical_path" == Plugins/example* || "$logical_path" == plugins/example* || "$logical_path" == plugins/generator ]]; then
        status="remove"
        candidates="docs/migration/Reference/PLUGINS.md"
        evidence="This is a development/plugin-SDK sample rather than a shipped operator workflow. The portable plugin lifecycle, API, trust model and buildable source pattern are documented in docs/migration/Reference/PLUGINS.md; runtime compilation of loose trusted C# is deliberately not shipped."
      elif [[ "$logical_path" == "Antenna/TrackerGeneric" ]]; then
        status="replace"
        candidates="Services/AntennaTrackerOutputs.cs;ViewModels/AntennaTrackerUIViewModel.cs"
        evidence="All three official antenna-tracker output protocols are implemented by the cross-platform tracker service and covered by protocol/lifecycle tests."
      elif [[ "$logical_path" == "Antenna/TrackerUI" ]]; then
        status="replace"
        candidates="Views/AntennaTrackerUIView.axaml;ViewModels/AntennaTrackerUIViewModel.cs;Services/AntennaTrackerOutputs.cs"
        evidence="The native Avalonia tracker view ports live target tracking and all official output interfaces; see docs/migration/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Grid/* ]]; then
        status="replace"
        candidates="Views/GridUIView.axaml;ViewModels/GridUIViewModel.cs;ViewModels/SurveyMissionBuilder.cs;Services/SurveyGridSupport.cs"
        evidence="Survey Grid UI, mission generation, split flights, camera profiles and grid persistence are ported and regression-tested; see docs/migration/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Joystick/* ]]; then
        status="replace"
        candidates="GCSViews/ConfigurationView/ConfigJoystickView.axaml;ViewModels/GCSViews/ConfigurationView/ConfigJoystickViewModel.cs;Services/JoystickControl.cs;Services/JoystickInput.cs"
        evidence="Joystick setup/actions and the application-level sender have native Linux, Windows and macOS backends with lifecycle/mapping tests; see docs/migration/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Controls/Icon/* ]]; then
        status="replace"
        candidates="Theme/MpTheme.axaml;GCSViews/FlightPlannerView.axaml;Controls/FlightPlannerMap.cs"
        evidence="The GDI icon helpers are replaced by Avalonia vector/font icons and native map drawing."
      elif [[ "$logical_path" == Controls/PreFlight/* ]]; then
        status="replace"
        candidates="Services/PreflightChecklist.cs;GCSViews/FlightDataView.axaml;ViewModels/FlightDataViewModel.cs"
        evidence="The checklist schema, evaluator, manual checks and native editor are ported and tested; see docs/migration/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Log/* ]]; then
        status="replace"
        candidates="Views/LogBrowseView.axaml;ViewModels/LogBrowseViewModel.cs;Views/MavlinkLogWindow.axaml;ViewModels/MavlinkLogConvertViewModel.cs;Services/TlogExportService.cs"
        evidence="DataFlash browsing, download/index and telemetry-log conversion/export workflows are implemented by native Avalonia views/services and tested; see docs/migration/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == GeoRef/* ]]; then
        status="replace"
        candidates="Views/GeoRefView.axaml;ViewModels/GeoRefViewModel.cs"
        evidence="The GeoRef workflow, matching modes, reports and EXIF output are ported and tested; see docs/migration/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == "MagCalib" ]]; then
        status="replace"
        candidates="Views/OfflineMagFitWindow.axaml;ViewModels/OfflineMagFitViewModel.cs;Services/OfflineMagFitService.cs"
        evidence="Offline magnetometer calibration for tlog/bin/log is implemented and tested; see docs/migration/Reference/PORT_STATUS.md."
      elif [[ "$logical_path" == Swarm/* ]]; then
        status="replace"
        candidates="Views/FormationControlWindow.axaml;ViewModels/FormationControlViewModel.cs;Views/SwarmFollowPathWindow.axaml;ViewModels/SwarmFollowPathViewModel.cs;Views/SwarmFollowLeaderWindow.axaml;ViewModels/SwarmFollowLeaderViewModel.cs;Views/SwarmWaypointLeaderWindow.axaml;ViewModels/SwarmWaypointLeaderViewModel.cs;Views/SwarmSequenceWindow.axaml;ViewModels/SwarmSequenceViewModel.cs"
        evidence="Formation, Follow Path, Follow Leader, Waypoint Leader and Sequence workflows are ported with exact-link safety and controller tests; see docs/migration/Reference/PORT_STATUS.md."
      else
        candidates="$(find_candidates "$native_path")"
      fi
      ;;
  esac

  printf '%s\t%s\t%s\t%s\t%s\n' \
    "$native_path" "$kind" "$status" "$candidates" "$evidence" >> "$native_output"
done < "$native_files"

printf 'source_path\tplanned_native_path\taction\tnotes\n' > "$import_output"
while IFS= read -r source_path; do
  target_path=""
  action="review"
  notes="Confirm semantic mapping before import."

  case "$source_path" in
    external/MissionPlanner)
      action="remove"
      notes="Never import the old Mission Planner gitlink; native source already is the repository root."
      ;;
    external/Directory.Build.props|external/Directory.Packages.props)
      action="remove"
      notes="Old submodule shielding is unnecessary in the in-place layout; scope root build policy explicitly instead."
      ;;
    .gitmodules)
      target_path=".gitmodules"
      action="merge"
      notes="Do not overwrite the native ExtLibs/mono submodule declaration."
      ;;
    src/MissionPlannerAvalonia/MissionPlannerAvalonia.csproj)
      target_path="MissionPlanner.csproj"
      action="merge"
      notes="Root project replacement; rename main assembly/product to MissionPlanner and use direct native paths."
      ;;
    src/MissionPlannerAvalonia/Program.cs)
      target_path="Program.cs"
      action="replace"
      notes="Root Avalonia entry point."
      ;;
    src/MissionPlannerAvalonia/Views/GCSViews/*)
      target_path="GCSViews/${source_path#src/MissionPlannerAvalonia/Views/GCSViews/}"
      action="import"
      notes="Avalonia replacement in the native GCSViews feature tree."
      ;;
    src/MissionPlannerAvalonia/Views/FlightDataView.*|src/MissionPlannerAvalonia/Views/FlightPlannerView.*|src/MissionPlannerAvalonia/Views/SetupView.*)
      target_path="GCSViews/${source_path#src/MissionPlannerAvalonia/Views/}"
      action="import"
      notes="Top-level operational view placed in the native GCSViews tree."
      ;;
    src/MissionPlannerAvalonia/Views/Setup/*)
      target_path="GCSViews/Setup/${source_path#src/MissionPlannerAvalonia/Views/Setup/}"
      action="import"
      notes="Setup implementation placed under the native GCSViews tree."
      ;;
    src/MissionPlannerAvalonia/*)
      target_path="${source_path#src/MissionPlannerAvalonia/}"
      action="import"
      notes="Port-owned application source/resource imported into the native root."
      ;;
    src/MissionPlannerAvalonia.PluginApi/*)
      target_path="Plugin/PortableApi/${source_path#src/MissionPlannerAvalonia.PluginApi/}"
      action="import"
      notes="Keep a distinct portable contract identity; rename project and references deliberately."
      ;;
    src/MissionPlannerAvalonia.LegacyPluginApi/*)
      target_path="Plugin/LegacyCompatibility/${source_path#src/MissionPlannerAvalonia.LegacyPluginApi/}"
      action="merge"
      notes="Merge compatibility types into main MissionPlanner assembly; do not emit a second MissionPlanner.dll."
      ;;
    src/Px4Uploader/*)
      target_path="ExtLibs/px4uploader/${source_path#src/Px4Uploader/}"
      action="merge"
      notes="Merge portable uploader changes into the native ExtLib project."
      ;;
    src/UpstreamCompat/MonoRuntimeSettingsCompatibility.cs)
      target_path="ExtLibs/Utilities/Compatibility/MonoRuntimeSettingsCompatibility.cs"
      action="merge"
      notes="Retain only if CoreCLR compatibility remains necessary after direct native changes."
      ;;
    tests/*)
      target_path="MissionPlannerTests/Avalonia/${source_path#tests/}"
      action="import"
      notes="Preserve all port regression coverage while unifying the test graph."
      ;;
    docs/*)
      target_path="docs/migration/Reference/${source_path#docs/}"
      action="import"
      notes="Historical/reference documentation; reconcile current facts into native docs/README later."
      ;;
    build/*|srtm/*)
      target_path="$source_path"
      action="import"
      notes="Cross-platform build/package/runtime support."
      ;;
    .github/*|.editorconfig|.gitattributes|.gitignore|Directory.Build.props|Directory.Build.targets|Directory.Packages.props|README.md|LICENSE|NOTICE.md|Makefile|global.json|SITL-TESTING.md)
      target_path="$source_path"
      action="merge"
      notes="Merge by meaning; never overwrite native repository policy, licensing or workflows mechanically."
      ;;
    MissionPlannerAvalonia.slnx)
      target_path="MissionPlanner.slnx"
      action="merge"
      notes="Create the cross-platform solution graph with direct root ExtLib references."
      ;;
    LICENSES/*)
      target_path="$source_path"
      action="import"
      notes="Retain third-party notices for shipped native components."
      ;;
  esac

  printf '%s\t%s\t%s\t%s\n' "$source_path" "$target_path" "$action" "$notes" >> "$import_output"
done < "$port_files"

mkdir -p "$output_dir"
mv "$native_output" "$output_dir/NATIVE_SURFACE.tsv"
mv "$import_output" "$output_dir/PORT_SOURCE_IMPORT.tsv"

native_rows="$(( $(wc -l < "$output_dir/NATIVE_SURFACE.tsv") - 1 ))"
source_rows="$(( $(wc -l < "$output_dir/PORT_SOURCE_IMPORT.tsv") - 1 ))"
echo "Generated $native_rows native rows and $source_rows source rows."
