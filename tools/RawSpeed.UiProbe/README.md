# WPF component rendering probe

This probe loads the application's actual resources and `MainWindow.xaml`, detaches the window's content into an offscreen visual host, and uses WPF `Measure`/`Arrange` and `RenderTargetBitmap` to verify component layout and rendering. It never calls `Window.Show`, starts a desktop UI automation session, injects input, invokes native window APIs, or opens application dialogs.

```powershell
dotnet run --project tools/RawSpeed.UiProbe -c UiProbe -p:Optimize=true -- artifacts/ui
```

The generated report and PNGs cover empty/loaded layouts, command bindings, photo fitting, histogram and curve rendering, EXIF-tab bindings and search, minimum supported layout dimensions, and foreground/background style regressions. Binding warnings and errors are captured in `binding-trace.txt` and fail the probe. The fixture is a generated gradient landscape used for component verification; it is not a camera sample or an output-quality benchmark.

`window-render.png` is an **in-process component render**, not a screenshot of a running desktop window. It cannot verify native window chrome, operating system dialogs, keyboard/mouse input routing, display scaling, graphics-driver behavior, or desktop accessibility. Those require a separate real desktop verification run.
