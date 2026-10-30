# aissyst.desktop

Avalonia desktop application scaffold targeting .NET 10. It currently opens a single starter window using the Fluent theme and the system theme preference.

```sh
dotnet run --project aissyst.desktop/aissyst.desktop.csproj
```

`Program.cs` configures Avalonia and the classic desktop application lifetime. `App.axaml` defines application styles. `Views/MainWindow.axaml` is the initial window. Shutdown occurs when the last window closes, allowing future windows to have independent lifetimes.

The project references `aissyst.core`, `aissyst.sqlite`, the platform audio projects, and `chatgpt-connector`. It does not depend on `aissyst.console`. Audio capture, session/model selectors, authorization, transcription output, and LLM controls still need to be connected to the GUI. The starter window does not initialize these services or make network requests.

The layout and behavior of the planned floating windows will be defined separately. The console application remains the functional interface in the meantime.
