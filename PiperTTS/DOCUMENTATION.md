# 🎙️ Piper TTS pro Unity – Kompletní dokumentace a API návod

Profesionální, 100% offline systém pro generování české i cizojazyčné řeči přímo v Unity běžící na CPU v reálném čase. Využívá nativní neurální modely VITS (ONNX) přes **Unity Inference Engine / Sentis** a fonetickou knihovnu **eSpeak-NG**.

---

## 📑 Obsah
1. [Architektura a princip fungování](#1-architektura-a-princip-fungování)
2. [Instalace do nového projektu](#2-instalace-do-nového-projektu)
3. [Rychlé zprovoznění v Unity (Krok za krokem)](#3-rychlé-zprovoznění-v-unity-krok-za-krokem)
4. [Kompletní API Reference (C#)](#4-kompletní-api-reference-c)
5. [Praktické herní příklady (Kódy do hry)](#5-praktické-herní-příklady-kódy-do-hry)
   - [A. Sekvence dialogu (Čekání na doznění)](#a-sekvence-dialogu-čekání-na-doznění)
   - [B. 3D prostorový zvuk na postavě / NPC](#b-3d-prostorový-zvuk-na-postavě--npc)
   - [C. Synchronizace s titulky v UI](#c-synchronizace-s-titulky-v-ui)
   - [D. Hlasové styly (Worms, Drsný voják, Rychlý skřítek)](#d-hlasové-styly-worms-drsný-voják-rychlý-skřítek)
   - [E. Efekt vysílačky / helmy přes AudioMixer](#e-efekt-vysílačky--helmy-přes-audiomixer)
6. [Stahování dalších hlasů](#6-stahování-dalších-hlasů)
7. [Časté chyby a řešení (Troubleshooting)](#7-časté-chyby-a-řešení-troubleshooting)

---

## 1. Architektura a princip fungování

Celý pipeline probíhá lokálně bez internetu v následujících krocích:
```
Text (string)
  │
  ▼
[PreprocessText]               // Nahrazení zkratek (HP -> životy, XP -> zkušenosti, DMG -> poškození)
  │
  ▼
[eSpeak-NG Native C Library]   // Převod textu na IPA fonémy (ošetřeno smyčkou pro neomezenou délku a interpunkci !, ?, .)
  │
  ▼
[Piper Tokenizer]              // Převod fonémů na ID tokenů dle JSON konfigurace modelu
  │
  ▼
[Unity Inference / Sentis]     // Běh VITS ONNX neuronové sítě na CPU (rozděleno do frame budgetu, žádný zásek hry)
  │
  ▼
[Audio Post-Processing]        // Odstranění DC offsetu, normalizace hlasitosti na 92 %, 12ms anti-click fade-in/out
  │
  ▼
Unity AudioClip                // Hotový zvuk připravený k okamžitému přehrání v AudioSource
```

---

## 2. Instalace do nového projektu

### Krok 1: Balíček pro Inference v Unity
Otevři v Unity **Window -> Package Manager**:
* Klikni na tlačítko **`+`** -> **`Add package by name...`**
* Pro **Unity 6**: zadej `com.unity.ai.inference`
* Pro **Unity 2022.3 LTS / 2023**: zadej `com.unity.sentis`

### Krok 2: Zkopírování souborů
Z této složky zkopíruj složku `Assets` přímo do kořene tvého Unity projektu:
* `Assets/Piper/` (obsahuje C# skripty, DLL knihovnu, demo a `.onnx` modely).
* `Assets/StreamingAssets/Piper/` (obsahuje slovníky `espeak-ng-data` a `.onnx.json` konfigurace).

> [!IMPORTANT]
> Složka `Assets/StreamingAssets/Piper` je pro běh nezbytná! `espeak-ng.dll` z ní čte fonetická data a `PiperTTS` z ní čte vzorkovací frekvenci a parametry hlasu.

---

## 3. Rychlé zprovoznění v Unity (Krok za krokem)

1. Vytvoř v hierarchii nový **GameObject** (např. pojmenovaný `TTS_Manager` nebo `NPC_Vojak`).
2. Přidej na něj komponentu **`AudioSource`**.
3. Přidej na něj komponentu **`PiperTTS`**.
4. Komponenta automaticky najde nainstalované modely v `Assets/Piper/Model/`. Ve výchozím stavu je aktivní `cs_CZ-jirka-medium`.
5. Pro rychlý test spusť scénu `Assets/Piper/Demo/PiperDemo.unity` a zmáčkni **Play**.

---

## 4. Kompletní API Reference (C#)

Hlavní třída je `Piper.PiperTTS` (dědí z `MonoBehaviour`).

### Vlastnosti (Properties)

| Vlastnost | Typ | Popis |
| :--- | :--- | :--- |
| `IsReady` | `bool` | Vrací `true`, jakmile je model zkompilován a eSpeak inicializován. |
| `ActiveVoice` | `string` | Název aktuálně načteného hlasu (např. `"cs_CZ-jirka-medium"`). |
| `VoiceNames` | `IReadOnlyList<string>` | Seznam všech dostupných hlasů nalezených v projektu. |
| `lengthScale` | `float` | Rychlost řeči. Vyšší hodnota = pomalejší, klidnější mluva. Doporučeno: `1.10f - 1.25f`. |
| `noiseScale` | `float` | Variabilita intonace. Výchozí: `0.72f`. Nižší = monotónnější, vyšší = výraznější intonace. |
| `noiseW` | `float` | Variabilita délky jednotlivých hlásek. Výchozí: `0.80f`. |
| `cacheClips` | `bool` | Při `true` ukládá vygenerované klipy do paměti. Opakované hlášky se přehrají okamžitě bez inference. |
| `Config` | `PiperVoiceConfig` | Metadata z JSONu (vzorkovací frekvence, espeak jazyk atd.). |

### Události (Events)

| Událost | Typ | Popis |
| :--- | :--- | :--- |
| `OnVoiceChanged` | `Action<string>` | Vyvolána pokaždé, když se úspěšně dokončí načtení nebo přepnutí hlasu. |

### Metody (Methods)

#### 1. `Speak(string text, AudioSource target = null)`
Nejjednodušší synchronní volání. Spustí generování na pozadí a po dokončení okamžitě přehraje zvuk přes `AudioSource.PlayOneShot()`.
* Pokud nezadáš `target`, automaticky použije `GetComponent<AudioSource>()`.
```csharp
tts.Speak("Nepřítel na dvanácti hodinách!");
```

#### 2. `SpeakAsync(string text, AudioSource target = null)` *(Task)*
Asynchronní varianta. Počká na dokončení syntézy a spustí přehrávání.
```csharp
await tts.SpeakAsync("Granát! Kryjte se!");
```

#### 3. `SpeakAndWaitAsync(string text, AudioSource target = null)` *(Task)*
**Klíčová metoda pro dialogy a příběhové scény.**
Vygeneruje zvuk, spustí přehrávání a pozastaví kód přesně na tak dlouho, dokud zvuk nedozní (zohledňuje i změněný `AudioSource.pitch`).
```csharp
await tts.SpeakAndWaitAsync("Vítej ve velitelském stanu, vojíne.");
await tts.SpeakAndWaitAsync("Máme pro tebe nový úkol.");
```

#### 4. `Generate(string text, Action<AudioClip> onReady)`
Vygeneruje `AudioClip` a předá ho do callbacku. Vhodné, pokud chceš se zvukovým klipem manipulovat dál (např. uložit, analyzovat spektrum).
```csharp
tts.Generate("Testovací hláška", clip => 
{
    if (clip != null) Debug.Log($"Vygenerován klip o délce: {clip.length} s");
});
```

#### 5. `GenerateAsync(string text)` *(Task<AudioClip>)*
Asynchronní verze generování vracející přímo `AudioClip`.
```csharp
AudioClip clip = await tts.GenerateAsync("Tajná zpráva");
```

#### 6. `SwitchVoice(string voiceName)`
Za běhu přepne model na jiný hlas (např. ze seznamu `VoiceNames`).
```csharp
tts.SwitchVoice("cs_CZ-kasandra-medium"); // Přepnutí na ženský český hlas
```

---

## 5. Praktické herní příklady (Kódy do hry)

### A. Sekvence dialogu (Čekání na doznění)

Následující skript demonstruje sekvenční rozhovor dvou postav bez překrývání hlasů:

```csharp
using UnityEngine;
using System.Threading.Tasks;
using Piper;

public class DialogueManager : MonoBehaviour
{
    [SerializeField] private PiperTTS tts;
    [SerializeField] private AudioSource soldierSource;
    [SerializeField] private AudioSource commanderSource;

    public async Task PlayBriefing()
    {
        // 1. Promluví velitel (mužský hlas)
        tts.SwitchVoice("cs_CZ-jirka-medium");
        tts.lengthScale = 1.15f;
        commanderSource.pitch = 0.9f; // Hlubší autoritativní hlas
        await tts.SpeakAndWaitAsync("Vojíne! Jaký je stav v sektoru sedm?", commanderSource);

        // 2. Odpoví voják (ženský hlas)
        tts.SwitchVoice("cs_CZ-kasandra-medium");
        tts.lengthScale = 1.05f;
        soldierSource.pitch = 1.05f;
        await tts.SpeakAndWaitAsync("Generále, všude je klid. Žádný pohyb nezaznamenán!", soldierSource);

        // 3. Velitel uzavírá
        tts.SwitchVoice("cs_CZ-jirka-medium");
        await tts.SpeakAndWaitAsync("Rozumím. Pokračujte v hlídce.", commanderSource);
    }
}
```

---

### B. 3D prostorový zvuk na postavě / NPC

Aby hlas vycházel z konkrétního místa ve světě:
1. Nastav na `AudioSource` parametr **Spatial Blend** na `1.0` (3D).
2. Nastav **3D Sound Settings**:
   * Min Distance: `2`
   * Max Distance: `25`
   * Rolloff: `Logarithmic Rolloff`
3. Připoj skript přímo na NPC:

```csharp
using UnityEngine;
using Piper;

public class EnemyShout : MonoBehaviour
{
    private PiperTTS tts;
    private AudioSource audioSource;

    private void Awake()
    {
        tts = GetComponent<PiperTTS>();
        audioSource = GetComponent<AudioSource>();
    }

    public void OnPlayerSpotted(string playerName)
    {
        tts.Speak($"Támhle je {playerName}! Nenechte ho pláchnout!", audioSource);
    }
}
```

---

### C. Synchronizace s titulky v UI

Jak zobrazit titulky přesně po dobu, co postava mluví:

```csharp
using UnityEngine;
using TMPro;
using System.Threading.Tasks;
using Piper;

public class SubtitleSpeaker : MonoBehaviour
{
    [SerializeField] private PiperTTS tts;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private TextMeshProUGUI subtitleText;

    public async Task SayWithSubtitles(string text)
    {
        subtitleText.text = text;
        subtitleText.gameObject.SetActive(true);

        // Počkáme, až Piper domluví celou větu
        await tts.SpeakAndWaitAsync(text, audioSource);

        subtitleText.text = "";
        subtitleText.gameObject.SetActive(false);
    }
}
```

---

### D. Hlasové styly (Worms, Drsný voják, Rychlý skřítek)

Různé charaktery lze vytvořit úpravou parametrů `lengthScale`, `noiseScale` a `AudioSource.pitch`:

```csharp
// 1. Styl Červík / Worms (rychlý, pištivý, agresivní)
audioSource.pitch = 1.45f;
tts.lengthScale = 0.92f;
tts.noiseScale = 0.85f;
tts.Speak("Tohle ti nedaruju, ty červe!");

// 2. Styl Drsný voják (hluboký, klidný, vážný)
audioSource.pitch = 0.82f;
tts.lengthScale = 1.25f;
tts.noiseScale = 0.65f;
tts.Speak("Nepřátelská základna zničena. Vracím se na základnu.");

// 3. Přirozený vypravěč / Dabing (optimální srozumitelnost)
audioSource.pitch = 1.0f;
tts.lengthScale = 1.12f;
tts.noiseScale = 0.72f;
tts.Speak("Kapitola první: Cesta do neznáma.");
```

---

### E. Efekt vysílačky / helmy přes AudioMixer

Nejjednodušší trik, jak hlasu dodat 100% uvěřitelný vojenský styl a skrýt syntetický nádech:

1. V Unity vytvoř **Audio Mixer** (`Assets -> Create -> Audio -> Audio Mixer`), pojmenuj např. `GameMixer`.
2. Vytvoř skupinu (Group) s názvem `RadioVoice`.
3. Přidej na ni tyto efekty:
   * **Highpass**: Cutoff frequency = `450 Hz` (odstraní hluboké basy).
   * **Lowpass**: Cutoff frequency = `3400 Hz` (ořízne vysoké sykavky typické pro vysílačku).
   * **Distortion**: Distortion = `0.20` (jemné zkreslení mikrofonu).
4. V komponentě `AudioSource` přiřaď **Output** -> `RadioVoice`.
5. Zvuk bude okamžitě znít jako reálná taktická vysílačka z bojové helmy!

---

## 6. Dostupné české modely a stahování dalších hlasů

V balíčku jsou již předinstalovány tyto české modely:

| Model | Kvalita | Velikost | Popis |
| :--- | :--- | :--- | :--- |
| **`cs_CZ-honza-high`** | **High** | ~85 MB | **Nejvyšší kvalita.** Větší neuronová síť (hidden dimension 384/512), čistší artikulace a detailnější fonémy. *(Pozn.: Výchozí konfigurace v JSONu je již upravena na přirozené tempo length_scale 1.25 a čistý noise_scale 0.65, aby nemluvil přehnaně zbrkle).* |
| **`cs_CZ-jirka-medium`** | **Medium** | ~63 MB | Osvědčený a vyvážený standardní mužský hlas pro češtinu. |
| **`cs_CZ-kasandra-medium`** | **Medium** | ~63 MB | Příjemný a srozumitelný ženský český hlas. |
| **`en_US-lessac-medium`** | **Medium** | ~63 MB | Čistý neutrální anglický hlas (pro případ vícejazyčné hry). |

> ℹ️ **Proč neexistuje oficiální "Jirka High"?**  
> V oficiálním upstream repozitáři Rhasspy Piper existuje Jirka pouze v `low` a `medium`, protože původní trénovací nahrávky korpusu byly pořízeny ve 22 kHz. Proto komunita (Thomcles) vytvořila fine-tune **`cs_CZ-honza-high`**, který využívá větší modelovou kapacitu (High) a čistší monospeaker data.

### Jak stáhnout další hlasy přes Unity Editor:
1. V horní liště Unity klikni na **`Piper -> Download Voices...`**.
2. Vyber požadovaný hlas ze seznamu (včetně dalších jazyků) a klikni na **Download**.
3. Nástroj automaticky stáhne:
   * `.onnx` model do `Assets/Piper/Model/`
   * `.onnx.json` konfiguraci do `Assets/StreamingAssets/Piper/`

---

## 7. Časté chyby a řešení (Troubleshooting)

### 🔴 *Chyba: "espeak-ng-data not found: .../StreamingAssets/Piper/espeak-ng-data"*
* **Příčina:** Složka s fonetickými daty chybí v `StreamingAssets`.
* **Řešení:** Zkontroluj, že existuje cesta `Assets/StreamingAssets/Piper/espeak-ng-data/` a jsou v ní soubory slovníků (např. `cs_dict`, složka `lang/` atd.).

### 🔴 *Chyba: "No float output tensor found"*
* **Příčina:** Verze balíčku Sentis / Inference nerozpoznala výstupní vrstvu modelu.
* **Řešení:** V [`PiperTTS.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PiperTTS/Assets/Piper/Scripts/PiperTTS.cs) je již implementován fallback, který projde všechny výstupní tenzory (`output`, `audio` atd.). Ujisti se, že používáš podporovanou verzi balíčku `com.unity.ai.inference` (Unity 6) nebo `com.unity.sentis` (2022.3).

### 🔴 *Chyba: "DllNotFoundException: espeak-ng"*
* **Příčina:** Unity nemůže najít nativní knihovnu pro Windows.
* **Řešení:** Zkontroluj, že soubor `Assets/Piper/Plugins/Windows/espeak-ng.dll` je v projektu a v jeho Inspectoru je zaškrtnutá platforma **Standalone (Windows) x86_64**.

### 🟡 *Dotaz: Můžu generovat řeč na jiném vlákně (Multi-threading)?*
* **Odpověď:** Samotná inference Unity Inference Engine musí běžet na hlavním vlákně (je ale rozkouskována do frame budgetu ~8 ms přes `ScheduleIterable()`, takže hra nezamrzá a neseká se framerate).
