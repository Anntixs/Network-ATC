# Плагины Network-ATC

Плагин — это библиотека .NET 8 с публичным классом, реализующим `IAtcPlugin`. Network-ATC при запуске загружает все DLL из `%APPDATA%\Network-ATC\plugins` (включая подпапки). Каждая сборка загружается в свой контекст, поэтому зависимости разных плагинов не конфликтуют.

## Проект

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Сборку API предоставляет приложение, копировать её не нужно. -->
    <Reference Include="NetworkAtc.PluginApi">
      <HintPath>путь\к\NetworkAtc.PluginApi.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

## Минимальный плагин

```csharp
using NetworkAtc.Plugins;

public sealed class HelloPlugin : IAtcPlugin
{
    public string Name => "Hello";
    public string Version => "1.0";

    public void Initialize(IPluginHost host)
    {
        // Поле тега: {gskm} — путевая скорость в км/ч.
        host.RegisterTagField("gskm", "Скорость, км/ч", a => (a.GroundSpeed * 1.852).ToString("0"));

        // Команда: .hello
        host.RegisterCommand("hello", "поздороваться", args => $"Привет, {host.ControllerCallsign}!");

        // Событие: новый план полёта.
        host.FlightPlanUpdated += (_, a) => host.Log($"{a.Callsign}: {a.Departure} → {a.Destination}");
    }
}
```

## Что доступно

| Член `IPluginHost` | Назначение |
|---|---|
| `Aircraft`, `SelectedAircraft` | трафик и выбранный борт (`IAircraft`: позиция, высота, скорость, курс, код, план полёта, указания диспетчера; с API 1.2 — `Owner`, `HandoffFrom`/`HandoffTo`, `Sid`, `Star`, `DepartureRunway`, `ArrivalRunway`, `ClearanceReceived`) |
| `AircraftUpdated`, `AircraftRemoved`, `FlightPlanUpdated`, `MessageReceived`, `ConnectionChanged` | события |
| `RegisterTagField(key, description, value)` | поле `{key}` для шаблонов тегов |
| `RegisterTagFieldClick(key, (aircraft, right) => …)` | щелчок по полю тега (API 1.1); пользователь может переназначить действие в настройках |
| `RegisterCommand(name, description, handler)` | команда `.name`, возвращает текст ответа |
| `RegisterOverlay(IRadarOverlay)` | рисование поверх радара: `IRadarCanvas` — линии, полилинии, полигоны, круги, текст в географических координатах; оверлей включается в меню «Слои» |
| `RegisterAircraftAction(title, action)` | пункт контекстного меню борта (правый клик) |
| `SetHighlight(callsign, color)` | подсветка борта цветом |
| `SendRadioMessageAsync`, `SendPrivateMessageAsync` | отправка сообщений |
| `Log(text)` | строка в окне сообщений |
| `DataDirectory` | папка для файлов плагина |

Цвета задаются строками `#RRGGBB` или `#AARRGGBB`. Все обработчики вызываются в защищённом режиме: исключение в плагине попадает в лог и не останавливает радар.

Полный пример — [examples/SamplePlugin](../examples/SamplePlugin/SamplePlugin.cs).
