# Universal Unity C# Architecture & Systems Repository

Komplexní repozitář s **107 produkčními C# systémy pro Unity 6** pokrývajícími kompletní herní a simulační architekturu: pokročilou umělou inteligenci, fyziku a balistiku, automobilovou a leteckou simulaci, kybernetiku a virtuální stroje, procedurální generátory, ekonomiku, diplomacii a UI Toolkit nástroje inspektoru.

---

## 📚 Kompletní katalog skriptů
- Detailní textový katalog: [`KATALOG_SKRIPTU.txt`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/KATALOG_SKRIPTU.txt) a [`SEZNAM_SKRIPTU.txt`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/SEZNAM_SKRIPTU.txt)
- Interaktivní Markdown rejstřík s tabulkami: [`CATALOG.md`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/CATALOG.md)

---

## 📂 Přehled modulů repozitáře

Celkem **107 skriptů** uspořádaných v **15 funkčních modulech**:

- **`/AI/`** (9 skriptů) — *Umělá inteligence, vnímání, rozhodování a sociální grafy*
- **`/Navigation/`** (5 skriptů) — *Navigace, pathfinding a prostorové indexování*
- **`/TacticsAndCrowd/`** (3 skriptů) — *Formace jednotek, hejna a RTS řízení*
- **`/Simulation/`** (13 skriptů) — *Ekonomika, tovární výroba, trhy, frakce a simulace světa*
- **`/Infrastructure/`** (7 skriptů) — *Budování, silnice, křižovatky, železnice a sítě*
- **`/PhysicsAndCombat/`** (15 skriptů) — *Fyzika, balistika, aerodynamika, orbity, radary a destrukce*
- **`/Automotive/`** (8 skriptů) — *Fyzika vozidel, motory, diferenciály, pneumatiky a telemetrie*
- **`/Aviation/`** (9 skriptů) — *Letecké simulace, motory, fly-by-wire, dogfight a ATC*
- **`/Cybernetics/`** (4 skriptů) — *Logické obvody, virtuální VM počítače a sítě*
- **`/PlayerAndControls/`** (5 skriptů) — *Ovládání hráče, vstupy, statistiky a kamera*
- **`/Core/`** (10 skriptů) — *Architektura, event bus, inventář, procedurální generátory a inspektor*
- **`/AudioAndVFX/`** (6 skriptů) — *Vizuální efekty, mlha války, syntéza zvuku a rytmika*
- **`/Assets/Editor/`** (4 skriptů) — *UI Toolkit nástroje: EditorProWorkspace & VisualInspectorStudio*
- **`/PiperTTS/`** (7 skriptů) — *Lokální offline neurální syntéza řeči Piper (Text-to-Speech)*
- **`/PixelLight2D/`** (2 skriptů) — *2D pixel-artové dynamické osvětlení*

---

## 🌟 Klíčové systémy repozitáře

### 1. `VisualInspectorStudio` (`/Assets/Editor/VisualInspectorStudio/` a `/Core/`)
- Moderní inspektor postavený na **Unity 6 UI Toolkit** (inspirován balíčkem Odin Inspector) s nulovými závislostmi.
- Podpora atributů `[Button]`, `[ShowIf]`, `[HideIf]`, `[ProgressBar]`, `[MinMaxSlider]`, `[BoxGroup]`, `[TabGroup]`, `[RequiredField]`, `[ReadOnlyInspector]` a `[TelemetryGraph]`.
- Automatické generování tlačítek pro metody, dynamické reaktivní skrývání polí a vizualizace telemetrických dat.

### 2. `EditorProWorkspace` (`/Assets/Editor/EditorProWorkspace/`)
- Třípanelový správce ScriptableObject aktiv v UI Toolkitu s podporou vyhledávání podle typů, dense mřížky, porovnávání aktiv a rychlé tvorby.

### 3. Fyzika, balistika a letecké/automobilové simulace
- Fyzikální modely pneumatik podle **Pacejka '96 Magic Formula**, termodynamika spalovacích motorů s turbem, více-nápravové přívěsy.
- Aerodynamika nosných ploch s mezním úhlem náběhu, **Fly-By-Wire** řízení, **Dogfight BFM** umělá inteligence a **ILS/ranvejový** dispečink.
- N-tělesová a Keplerova orbitální mechanika, penetrace pancíře s odrazy a střepinami (spalling) a destruovatelný terén Marching Squares.

### 4. Společnost, kybernetika a procedurální generace
- Virtuální in-game **CPU/VM** se sadou instrukcí, logická hradla a síťový hackerský terminál.
- Sociální a konspirační grafy, finanční kniha objednávek (Order Book), bankovní úvěrový systém a genetické křížení Mendelistických alel.
- Deterministická knihovna šumů (Simplex/Worley/Perlin/Xoshiro256) a Markovovský generátor jmen a lore.

---

## 🚀 Verifikace a kompilace
Projekt je plně zkompilován a validován pro Unity 6:
```bash
dotnet build .validation/Validator.csproj --no-incremental
```
Výsledek: **0 Errors, 0 Warnings**.

