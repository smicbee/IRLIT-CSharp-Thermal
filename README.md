# ThermalCamLib – Lock-In Thermografie mit UVC-Thermalkameras

**ThermalCamLib** ist eine C#/.NET-Framework-Bibliothek zur Ansteuerung von UVC-Thermalkameras (z. B. HT301 / T2S+) inklusive  
Live-View, Radiometric-Raw-Zugriff, Dark-Field-Korrektur, Bildaufbereitung und **Lock-In-Thermografie**.
ThermalLib ist die allgemeine Bibliothek, die sich um das Ansteuern und Auswerten der Thermalbilder kümmert. ThermalViewer ist die Demo-Applikation, die alle implementierten Features in einer Win32 GUI abbildet. Optional gibt es noch einen Sketch für einen ESP32C6, der sich um die einfache Ansteuerung und Kommunikation zwischen ThermalLib und dem ESP32C6-GPIO Ports kümmert.

Das Projekt richtet sich an Entwickler:innen und Forschungsanwendungen (NDT, Materialanalyse, Thermografie).

---

## ✨ Features

### Kamera & Bildaufnahme
- UVC-Ansteuerung über **DirectShow**
- Zugriff auf **16-bit Raw Thermal Frames**
- Live-View mit Auto-Contrast
- Zuschneiden der Kamera-Metadatenzeilen
- Temperaturkonvertierung (Raw → °C)
- Hot-Pixel-Korrektur
- Fokus-Metrik (Schärfebewertung aus Bilddaten)

### Bildverarbeitung
- 8-bit Graustufenbilder
- Farbabbildungen (Colormaps):
  - Grayscale
  - Iron
  - Hot
  - Jet
  - Rainbow
  - Turbo
  - Inferno
  - Viridis
- Optionaler Auto-Contrast oder feste Skalierung

### Dark- / Flat-Field-Korrektur
- Shutter-gestützte Dark-Frame-Aufnahme
- Subtraktion + Offset-Stabilisierung
- Optional auch als „Current Correction“

### Lock-In-Thermografie
- Synchrone Lock-In-Auswertung pro Pixel
- Unterstützung externer Stimuli (GPIO / COM / Shelly)
- Phase-Bin-basierte ON/OFF-Integration
- Berechnung von:
  - Amplitudenbild
  - Phasenbild
  - Winkelabhängigen Differenzbildern
- Export aller Phasenbilder (0–180°)
- Stabile Farbschalen über alle Winkel
- Abbruch & Fortschrittsanzeige (CancellationToken + Progress)

---



---

## 🚀 Schnellstart

### Kamera öffnen & Live-View
```csharp
var cam = new ThermalCamera(tempConverter);
cam.Open("T2S+");
cam.EnableRawMode();
cam.Start();

cam.FrameReceived += (s, frame) =>
{
    var img = frame.ToColorMappedImage(
        ThermalFrame.ColorMapType.Turbo,
        autoContrast: true);
};
```

### Dark-Field-Korrektur
```csharp
var dark = await cam.CaptureDarkFrameAsync();
darkCorr.SetDarkFrame(dark, metaRows: 4);
```

### Lock-In-Messung starten
```csharp
var result = await runner.RunAsync(
    stimulus: stimulusController,
    frequencyHz: 1.0,
    duration: TimeSpan.FromSeconds(10),
    metaRows: 4,
    dutyCycle: 0.5,
    framePreprocess: f => darkCorr.Apply(f),
    ct: cancellationToken
);
```


### Winkelabhängiges Lock-In-Bild
```csharp
var frame = result.Accumulator.GetFrameAtAngle(
    angleDeg: 45,
    useMean: true,
    offset: globalOffset,
    scale: globalScale
);
```

### Amplituden- & Phasenbilder
```csharp
var ampFrame   = result.Accumulator.GetAmplitudeFrame();
var phaseFrame = result.Accumulator.GetPhaseFrame();

var phaseColor = result.Accumulator.GetPhaseColorImage();
```


### Alle Phasen exportieren
```csharp
var frames = result.Accumulator.GetAllAngleFrames();
foreach (var kv in frames)
{
    kv.Value.ToColorMappedImage(ColorMapType.Turbo)
            .Save($"phi_{kv.Key:F1}.png");
}
```

### 🔌 Stimulus-Ansteuerung
Das Stimulus-Interface ist abstrahiert:
```csharp
public interface IStimulusController
{
    Task TurnOnAsync(CancellationToken ct);
    Task TurnOffAsync(CancellationToken ct);
}
```

### 🤝 Beiträge

Pull Requests, Issues und Ideen sind willkommen –
das Projekt ist bewusst modular und experimentell gehalten.


### Hintergrund

Dieses Projekt entstand iterativ aus praktischer Arbeit mit günstigen
Thermalkameras und dem Ziel, volle Kontrolle über Raw-Daten und Lock-In-Auswertung
ohne proprietäre SDKs zu erhalten.

