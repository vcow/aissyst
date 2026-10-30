# aissyst.desktop

Avalonia desktop application targeting .NET 10, with a startup wizard and a system log window. It uses the Fluent theme and the system theme preference.

```sh
dotnet run --project aissyst.desktop/aissyst.desktop.csproj
```

`Program.cs` configures Avalonia and the classic desktop application lifetime. `App.axaml` defines application styles. `Views/MainWindow.axaml` is the log window. Closing it cancels startup, awaits initialization, and asynchronously disposes the controller, model, connector, transcription engine, and remaining services before exiting.

The main window displays structured log entries in a virtualized ListBox on a dark gray background. Information messages are white, warnings yellow, and errors/critical messages red. Each entry retains its local timestamp, level, category, full message, and multiline exception details. Independent Info, Warning, and Error toggles are initially enabled; they hide entries without removing them. Error includes Critical. Selectable text inside each entry allows copying individual fragments.

Copy places a snapshot of the entire accumulated log on the clipboard, including messages hidden by filters and entries not yet rendered. Clear removes all stored, visible, and pending entries; subsequent messages continue to arrive and filter selections are preserved. There is no retention limit or automatic trimming, as requested. The log is held in memory until Clear or application exit. Updates from background loggers are collected into batches and dispatched to the UI, without rebuilding one large text string on every message. Formatting a full Copy snapshot runs in the background.

Automatic scrolling follows new entries while the view is at the bottom. Scrolling up pauses following, keeping the reading position stable as entries arrive. The Latest button appears while following is paused; it jumps directly to the last visible entry and resumes following. Manually scrolling back to the bottom also resumes following. The built-in ListBox scroll viewer and VirtualizingStackPanel are preserved.

`WindowLoggerProvider` implements `ILoggerProvider`; all application services receive their usual `ILogger<T>` from the desktop DI container and use this provider, including `ApplicationController`, Whisper, and the ChatGPT connector. The minimum level remains Information. The console uses its existing standard provider. Avalonia framework diagnostics still use `LogToTrace`.

`DesktopServices` registers the shared application controller, settings, SQLite store, text processor, Whisper transcriber, session model, and ChatGPT connector. The project does not depend on `aissyst.console`. `ApplicationSettings` now lives in `aissyst.core` and is used by both frontends; the console keeps its selectors, hotkeys, and standard console logging provider.

Startup follows the existing controller sequence:

1. Choose a saved session, or create one from an existing context or a ZIP template. Selecting a saved session shows Delete and Next (Open) on the same page; Create New shows the ordinary Next button. Delete requires inline confirmation, with Cancel returning to the existing choices without submitting a selection. Confirmed deletion removes that session and its dialog, then refreshes the list in the same window. Deleting the last session advances to context selection. Empty lists advance to context selection or template import. The file page supports a native file picker and an editable local path. Import errors appear on that page and in the log, allowing another attempt.
2. Sign in through the existing ChatGPT browser flow if necessary; choose an account-available model and its reasoning effort. Models without selectable levels use the server default.
3. Load Whisper, restore the selected dialog, and initialize the actual LLM session over the existing Responses connection.
4. Select a microphone and an application audio source. Either may be None, but at least one source must be selected. Selecting neither repeats source selection with an explanation.
5. Close the wizard and log that the LLM session is ready.

The context page likewise provides Delete and Next (Create) for saved contexts. Context deletion uses inline confirmation and refreshes the list; sessions already created from that context retain their copied data. Deleting the final context advances to template import. Create from Template uses the ordinary Next button.

`WizardService` implements the independent `IWizardService` UI contract. It creates one independent, nonmodal window on the first request, accepts requests from background threads, and serializes requests. Startup is queued after the log window has opened. The wizard has a fixed size of 620 × 520 logical pixels, enforced by matching minimum/maximum dimensions and disabled resizing. `DesktopWindowPlacement` places the log and wizard side by side in the screen's working area, reducing only the log window's width if needed. Both windows remain interactive and movable. Radio choices have a vertical scrollbar. Next confirms only the current page and immediately disables until another page arrives; there is no Previous button.

The wizard uses border-only window decorations without active native close/minimize/maximize buttons. Drag the separate, permanent `aissyst - Setup` title bar to move it; the title bar remains enabled while a page is waiting or has failed. Normal close requests are rejected, including programmatic `Close()` outside its explicit completion method. The desktop startup code completes the wizard only after successful `ApplicationController.InitializeAsync`, so selectors do not need to know the last step. Startup failure leaves the wizard open with an error and stops its waiting indicator. Closing the log window or choosing application Quit still cancels initialization and disposes the wizard as part of application shutdown. Close and restart the application to retry a startup failure.

Desktop and console share the SQLite location and ChatGPT credential store. Run one frontend at a time: the existing connector holds an exclusive credential-store lock. The Whisper model is downloaded into the desktop output directory if absent, so the first desktop startup may download it even if the console has its own copy. macOS is the supported audio platform; Windows enumeration/capture remains a placeholder and a startup error is logged there.

Startup progress reaches the wizard through an optional `IProgress<InitializationProgress>` in core and a desktop adapter; it does not parse logger messages. Downloads show a percentage and received/total decimal MB when Content-Length is known, or received MB and an indeterminate bar otherwise. Connecting to the download server, SHA-256 verification, native model loading, and LLM connection show their stage with an indeterminate bar. Download updates are throttled to about 200 ms, and the wizard keeps only the latest pending UI update. Page changes, failure, and completion discard queued progress from the previous page.

Normal logs retain download start, completion, cancellation, and failures, without periodic progress messages. Sanitized URLs, HTTP response metadata, timeout, and temporary paths remain available at Debug level. Errors preserve the exception and report the stage and bytes received. Download timeout settings, checksum verification, cleanup, and the model cache location are unchanged; automatic retry and resume are not added.

At this stage audio sources are configured but capture does not start. Recording controls, assistance buttons, and dedicated transcript/answer windows are future work. The console remains available for the complete interview workflow.

Headless UI tests exercise window reuse, immediate Next disabling, cancellation, long-list scrolling, and DI/logging plus controller initialization with a test LLM connector. Selector tests cover SQLite session creation, template retry, model effort, and audio-source validation. These tests do not access credentials or send real LLM requests.
