# 2D Pixel Light (URP)

> **Rychlé vysvětlení funkčnosti (max 5 vět):**
> 1. Tento 2D pixel-artový systém osvětlení využívá Universal Render Pipeline (URP) s 2D Rendererem, který má nastavené poloviční rozlišení světelné mapy (`Light Render Texture Scale = 0.5`) a cookie textury s Point filtrem pro zachování ostrých pixelových přechodů bez rozmazávání.
> 2. Hlavní objekt **Breath Light 2D** kombinuje bodové světlo (`Light2D Point`) s pixelovou cookie texturou `SpotLight` a zapnutými 2D stíny (`Shadows Enabled = 1`).
> 3. Vnořené světlo **Rim Light 2D** doplňuje parametrický obrysový svit, který vizuálně zvýrazňuje siluety herních spritů a odděluje je od okolního prostředí.
> 4. Organický efekt „dýchání“ plynule moduluje měřítko a intenzitu světla v čase pomocí matematické funkce sinus (`Mathf.Sin(Time.time * interval) * power`) skrze přiložený skript `BreathLight2D.cs` (nebo Visual Scripting).
> 5. Aby bodová světla a stíny správně vynikly, vyžaduje scéna tlumené ambientní světlo **Global Light 2D** a sprity renderované se shaderem `Universal RP/2D/Sprite-Lit-Default`.

---

## Obsah balíčku

- **`Prefabs/`**:
  - `Breath Light 2D.prefab` – Původní prefab z projektu Moka-Wars (s Visual Scripting embedded grafem).
  - `Pixel Light 2D (Scripted).prefab` – Samostatná verze s čistým C# skriptem `BreathLight2D.cs` (bez závislosti na Visual Scriptingu).
  - `Global Light 2D.prefab` – Prefab globálního ambientního osvětlení scény.
- **`Sprites/`**:
  - `SpotLight.png` + `.meta` – 64×64 pixel-artová cookie textura (Point filter, 16 PPU, zachováno původní GUID).
- **`Scripts/`**:
  - `BreathLight2D.cs` – C# skript pro plynulou modulaci velikosti a intenzity světla (Moka-Wars sinusová křivka).
  - `PixelLightAnimator.cs` – Rozšířený animátor nabízející i efekty louče/ohně (Flicker), blikající zářivky (Stutter) a pulzu (Pulse).
- **`Settings/`**:
  - `Renderer2D.asset` & `Renderer2D_Clean.asset` – URP 2D Renderer s nastavením `m_LightRenderTextureScale: 0.5`.
  - `UniversalRP.asset` – Pipeline asset pro URP 2D.
  - `DefaultVolumeProfile.asset` – Nastavení post-processingu (Bloom, Tonemapping) pro záření světel.

---

## Rychlé použití v Unity

1. Přetáhněte `Global Light 2D` do scény a nastavte barvu do tmavšího tónu (např. tmavě modrá/šedá, intenzita ~0.4–0.6).
2. Přetáhněte `Pixel Light 2D (Scripted)` nebo `Breath Light 2D` k objektu, který má svítit (hráč, louč, projektil).
3. Ujistěte se, že spritové materiály v projektu používají `Universal RP/2D/Sprite-Lit-Default`.
