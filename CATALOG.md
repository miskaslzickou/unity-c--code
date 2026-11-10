# 📚 Kompletní katalog a mapa všech skriptů (`unity-c--code`)

Tento dokument slouží jako interaktivní rejstřík a přehled všech **107 C# systémů a herních architektur** uspořádaných do logických složek podle domén.
Všechny skripty jsou plně otestovány a kompilovány v Unity 6 (.NET Standard 2.1).

---

## 📂 Struktura složek a modulů

```
unity-c--code/
├── AI                       # 9 skriptů: Umělá inteligence, vnímání, rozhodování a sociální grafy
├── Navigation               # 5 skriptů: Navigace, pathfinding a prostorové indexování
├── TacticsAndCrowd          # 3 skriptů: Formace jednotek, hejna a RTS řízení
├── Simulation               # 13 skriptů: Ekonomika, tovární výroba, trhy, frakce a simulace světa
├── Infrastructure           # 7 skriptů: Budování, silnice, křižovatky, železnice a sítě
├── PhysicsAndCombat         # 15 skriptů: Fyzika, balistika, aerodynamika, orbity, radary a destrukce
├── Automotive               # 8 skriptů: Fyzika vozidel, motory, diferenciály, pneumatiky a telemetrie
├── Aviation                 # 9 skriptů: Letecké simulace, motory, fly-by-wire, dogfight a ATC
├── Cybernetics              # 4 skriptů: Logické obvody, virtuální VM počítače a sítě
├── PlayerAndControls        # 5 skriptů: Ovládání hráče, vstupy, statistiky a kamera
├── Core                     # 10 skriptů: Architektura, event bus, inventář, procedurální generátory a inspektor
├── AudioAndVFX              # 6 skriptů: Vizuální efekty, mlha války, syntéza zvuku a rytmika
├── Assets/Editor            # 4 skriptů: UI Toolkit nástroje: EditorProWorkspace & VisualInspectorStudio
├── PiperTTS                 # 7 skriptů: Lokální offline neurální syntéza řeči Piper (Text-to-Speech)
├── PixelLight2D             # 2 skriptů: 2D pixel-artové dynamické osvětlení
```

---

## 1. /AI/ — Umělá inteligence, vnímání, rozhodování a sociální grafy

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`AIDirector.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/AIDirector.cs) | `AIDirector : MonoBehaviour` | `(Global)` | Dynamický režisér tempa hry a napětí (pacing). Řídí křivku napětí (BuildUp, Peak, Relief), generuje vlny nepřátel a přepady (ambushes) podle aktuálního stavu a výkonu hráče. |
| [`AIPerception.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/AIPerception.cs) | `AIPerception : MonoBehaviour` | `(Global)` | Senzorický systém vnímání pro AI agenty. Obsahuje zorný kužel (Sight Cone) s detekcí překážek přes Raycast2D/3D, sluchový rádius (Hearing Radius) a paměť pro dočasné zapamatování si ztracených cílů. |
| [`BehaviorTree.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/BehaviorTree.cs) | `BTNode`, `SelectorNode`, `SequenceNode`, `InverterNode`, `ActionNode` | `(Global)` | Vysoce výkonný rozhodovací strom (Behavior Tree) s kompozitními uzly, dekorátory a akčními uzly s podporou stavů Success, Failure a Running. |
| [`ContextSteering.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/ContextSteering.cs) | `ContextSteering : MonoBehaviour` | `GameSystems.AI` | Kontextové řízení pohybu (Context Behaviors). Používá polarizované mapy zájmu (Interest Maps) a nebezpečí (Danger Maps) pro plynulé obcházení překážek a manévrování v boji. |
| [`GOAPPlanner.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/GOAPPlanner.cs) | `GOAPPlanner : MonoBehaviour`, `GOAPAction`, `GOAPGoal`, `GOAPWorldState` | `(Global)` | Plánování akcí orientované na cíle (Goal-Oriented Action Planning). Využívá A* stavový prohledávač pro dynamické sestavení optimální posloupnosti akcí k dosažení zadaného cíle. |
| [`HierarchicalStateMachine.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/HierarchicalStateMachine.cs) | `HierarchicalStateMachine`, `State`, `StateTransition` | `GameSystems.FSM` | Hierarchický konečný automat (HFSM) s podporou vnořených substavů, paralelních větví, podmínek přechodu a globálních prioritních triggerů pro komplexní chování bossů. |
| [`InfluenceMap.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/InfluenceMap.cs) | `InfluenceMap : MonoBehaviour` | `GameSystems.Tactical` | Taktická vlivová mřížka (Influence Map). Šíří hodnoty hrozeb (Threat), přátelského vlivu (Friendly) a zranitelnosti terénu pro strategické rozhodování taktické AI. |
| [`SocialNetworkAndConspiracyGraph.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/SocialNetworkAndConspiracyGraph.cs) | `SocialNetworkAndConspiracyGraph : MonoBehaviour`, `SocialNode`, `SocialEdge`, `ConspiracyCell` | `(Global)` | Sociální vztahový a konspirační graf entit. Modeluje přátelství, rivalitu, důvěru, šíření fám (rumor propagation) a odhalování tajných buněk spikleneckých frakcí. |
| [`UtilityBrain.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AI/UtilityBrain.cs) | `UtilityBrain : MonoBehaviour`, `UtilityAction`, `UtilityCurve` | `(Global)` | Rozhodovací systém založený na teorii užitku (Utility AI). Dynamicky vyhodnocuje matematické odezvové křivky (Response Curves) a vybírá nejvýhodnější akci v proměnlivém herním prostředí. |

---

## 2. /Navigation/ — Navigace, pathfinding a prostorové indexování

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`AStarPathfinder.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Navigation/AStarPathfinder.cs) | `AStarPathfinder : MonoBehaviour`, `PathNode` | `UniversalAStarPathfinding` | Univerzální A* algoritmus pro vyhledávání cest v 2D (Top-Down i Platformer se skoky a gravitací) a 3D mřížkách s diagonálním pohybem a Catmull-Rom vyhlazováním. |
| [`FlowFieldPathfinder.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Navigation/FlowFieldPathfinder.cs) | `FlowFieldPathfinder : MonoBehaviour`, `FlowFieldCell` | `GameSystems.Navigation` | Vektorové tokové pole (Flow Field). Počítá integrační pole a směrové vektory pro simultánní navigaci tisíců jednotek k jednomu nebo více cílům bez poklesu výkonu. |
| [`RVO2Simulator.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Navigation/RVO2Simulator.cs) | `RVO2Simulator : MonoBehaviour`, `RVOAgent`, `Obstacle2D` | `GameSystems.Crowd` | Reciprocal Velocity Obstacles (RVO2) algoritmus pro lokální vyhýbání se kolizím mezi stovkami samostatných agentů v reálném čase bez vzájemného zasekávání a kolísání rychlostí. |
| [`SpatialHashGrid.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Navigation/SpatialHashGrid.cs) | `SpatialHashGrid<T>` | `GameSystems.Spatial` | Ultrarychlá generická prostorová hašovací mřížka pro 2D/3D prostor. Nahrazuje náročná OverlapCircle volání dotazy v konstantním čase O(1) (QueryRadius, QueryAABB). |
| [`UniversalPathfindingAgent.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Navigation/UniversalPathfindingAgent.cs) | `UniversalPathfindingAgent : MonoBehaviour` | `UniversalAStarPathfinding` | Samostatná komponenta agenta pro plynulý pohyb po nalezené trase s adaptivním zpomalováním v zatáčkách, fyzikálním řízením přes Rigidbody a detekcí dosažení waypointů. |

---

## 3. /TacticsAndCrowd/ — Formace jednotek, hejna a RTS řízení

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`FlockingBrain.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/TacticsAndCrowd/FlockingBrain.cs) | `FlockingBrain : MonoBehaviour`, `BoidAgent` | `(Global)` | Reynoldsovo hejnové chování (Boids). Implementuje síly separace, soudržnosti (cohesion), zarovnání směru (alignment) a únik před predátory pro ptačí hejna, hejna ryb i rojové drony. |
| [`FormationBrain.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/TacticsAndCrowd/FormationBrain.cs) | `FormationBrain : MonoBehaviour`, `FormationPattern` | `(Global)` | Taktické vojenské formace jednotek (Box, Wedge, Line, Circle, Echelon). Dynamicky přiřazuje sloty jednotkám, otáčí formaci ve směru pochodu a adaptivně udržuje rozestupy. |
| [`RTSSelectionAndGroupManager.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/TacticsAndCrowd/RTSSelectionAndGroupManager.cs) | `RTSSelectionAndGroupManager : MonoBehaviour` | `GameSystems.RTS` | RTS systém výběru jednotek myší (obdélníkový drag-box selection), ukládání do kontrolních skupin (klávesy 0-9), přidávání jednotek přes Shift a hromadné zadávání rozkazů k pohybu. |

---

## 4. /Simulation/ — Ekonomika, tovární výroba, trhy, frakce a simulace světa

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`BankingCreditAndInflationEngine.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/BankingCreditAndInflationEngine.cs) | `BankingCreditAndInflationEngine : MonoBehaviour`, `LoanContract`, `BankEntity` | `GameSystems.Economy` | Centrální a komerční bankovní systém. Simuluje frakční rezervy, emisi úvěrů s úrokovou mírou, dluhopisy, míru inflace a vliv nabídky peněz na tržní ceny. |
| [`CellularAutomataSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/CellularAutomataSim.cs) | `CellularAutomataSim : MonoBehaviour` | `GameSystems.Simulation` | Vysoce optimalizovaný 2D simulátor buněčných automatů na plochém 1D poli. Simuluje šíření plamenů, kouře, proudění kapalin/vody a rozptyl environmentálního znečištění. |
| [`ContrabandAndCustomsInspectionSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/ContrabandAndCustomsInspectionSim.cs) | `ContrabandAndCustomsInspectionSim : MonoBehaviour`, `ContrabandDefinition`, `CargoContainer` | `GameSystems.Economy` | Celní a inspekční kontrolní systém pašovaného zboží. Modeluje falešná dna kufrů, rentgenové skenery, detekční psy, podplácení celníků a dynamiku černého trhu. |
| [`CrimeSceneAndForensicsTracker.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/CrimeSceneAndForensicsTracker.cs) | `CrimeSceneAndForensicsTracker : MonoBehaviour`, `EvidenceRecord`, `ClueChain` | `GameSystems.Forensics` | Detektivní a forenzní systém vyšetřování místa činu. Sleduje otisky prstů, balistické rýhy, krevní stříkance, čas rozkladu důkazů a rekonstrukci řetězce událostí. |
| [`DynamicFactionAndTreatyEngine.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/DynamicFactionAndTreatyEngine.cs) | `DynamicFactionAndTreatyEngine : MonoBehaviour`, `FactionEntity`, `DiplomaticTreaty` | `(Global)` | Diplomatický systém frakcí a mezinárodních smluv. Zahrnuje aliance, pakty o neútočení, obchodní dohody, vyhlašování válek a matici reputačních vztahů. |
| [`DynamicMarketEconomy.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/DynamicMarketEconomy.cs) | `DynamicMarketEconomy : MonoBehaviour`, `MarketPool<T>`, `ITradableAsset`, `PhysicalResourceAsset` | `GameSystems.Economy` | Generická tržní simulace nabídky a poptávky. Obsahuje vzorce pro výpočet cenové rovnováhy, komoditní burzu, akcie a kruhový buffer pro cenové grafy. |
| [`EcosystemEvolutionSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/EcosystemEvolutionSim.cs) | `EcosystemEvolutionSim : MonoBehaviour`, `OrganismEntity` | `(Global)` | Biologická simulace ekosystému a evoluce organismů. Zahrnuje potravní řetězce, energii, věk, reprodukci a náhodné genetické mutace fyzických i chování vlastností. |
| [`EpidemicAndPathogenSpreadSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/EpidemicAndPathogenSpreadSim.cs) | `EpidemicAndPathogenSpreadSim : MonoBehaviour`, `HostEntity`, `PathogenStrain` | `(Global)` | Epidemiologický model SEIR (Susceptible, Exposed, Infected, Recovered). Simuluje šíření patogenů vzduchem/kontaktem, virové mutace, imunitní odezvu a karanténní opatření. |
| [`GeneticsAndTraitCrossbreeder.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/GeneticsAndTraitCrossbreeder.cs) | `GeneticsAndTraitCrossbreeder : MonoBehaviour`, `Genome`, `GeneTrait` | `(Global)` | Mendelistická genetika a křížení vlastností. Obsahuje dominantní a recesivní alely, genovou rekombinaci, fenotypické vyjádření barvy/velikosti a mutační odchylky. |
| [`OrderBookAndMatchingEngine.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/OrderBookAndMatchingEngine.cs) | `OrderBookAndMatchingEngine : MonoBehaviour`, `AssetOrderBook`, `OrderEntry` | `GameSystems.Economy` | Finanční kniha objednávek (Order Book) a párovací motor burzy. Zpracovává Limit i Market příkazy, udržuje fronty nákupů a prodejů podle ceny a času (FIFO). |
| [`ProductionChainNode.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/ProductionChainNode.cs) | `ProductionChainNode : MonoBehaviour`, `ProductionRecipeSO` | `GameSystems.Factory` | Výrobní uzel pro továrny a dílny (ve stylu Factorio). Zpracovává recepty, spravuje vstupní a výstupní sklady, spotřebovává energii/vodu a generuje emise. |
| [`ThermalAndCoolantLoopSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/ThermalAndCoolantLoopSim.cs) | `ThermalAndCoolantLoopSim : MonoBehaviour`, `ThermalNode`, `CoolantLoop` | `GameSystems.Thermal` | Termodynamická simulace generování tepla a chladicích smyček. Modeluje radiátory, tepelnou vodivost komponent, čerpadla chladiva a přehřívání reaktorů. |
| [`TickSimulationManager.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Simulation/TickSimulationManager.cs) | `TickSimulationManager : MonoBehaviour`, `ITickable` | `GameSystems.Simulation` | Deterministické simulační hodiny s pevnou frekvencí nezávislou na snímkové frekvenci (FPS). Rozděluje výpočetní zátěž (time-slicing) mezi framy pro 10 000+ entit. |

---

## 5. /Infrastructure/ — Budování, silnice, křižovatky, železnice a sítě

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`AutoTileBitmasker.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Infrastructure/AutoTileBitmasker.cs) | `AutoTileBitmasker : MonoBehaviour`, `RuleTileSetSO` | `GameSystems.Tiles` | Algoritmus bitmaskingu pro autotiling dlaždic (Wang 47-tile a 16-tile). Automaticky vybírá správné rohové a hranové sprity podle okolních 8 sousedů. |
| [`GridPlacementSystem.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Infrastructure/GridPlacementSystem.cs) | `GridPlacementSystem : MonoBehaviour` | `GameSystems.Construction` | Mřížkový stavební systém budov. Zahrnuje kolizní kontrolu terénu, otáčení objektů v 90° krocích, ghost preview model s barevným značením validity a okamžité stavění. |
| [`RailBlockSignalingSystem.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Infrastructure/RailBlockSignalingSystem.cs) | `RailBlockSignalingSystem : MonoBehaviour`, `RailSignalNode` | `GameSystems.Railways` | Železniční zabezpečovací systém blokových návěstidel. Udržuje rezervace úseků kolejí, brání srážkám vlaků a bezpečně řídí průjezd výhybkami. |
| [`ResourceNetworkGraph.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Infrastructure/ResourceNetworkGraph.cs) | `ResourceNetworkGraph : MonoBehaviour`, `NetworkNode`, `NetworkEdge` | `GameSystems.Utilities` | Grafová distribuční síť pro elektrická vedení a vodovodní potrubí. Využívá BFS algoritmus pro efektivní přenos komodit od elektráren/zdrojů ke spotřebičům. |
| [`SplineRoadBuilder.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Infrastructure/SplineRoadBuilder.cs) | `SplineRoadBuilder : MonoBehaviour` | `GameSystems.Roads` | Procedurální generátor silnic a cest ze spline křivek. Generuje 3D/2D mesh s plynulým Catmull-Rom vyhlazením, UV mapováním a křížením vozovek. |
| [`TrafficIntersectionController.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Infrastructure/TrafficIntersectionController.cs) | `TrafficIntersectionController : MonoBehaviour`, `TrafficPhase` | `GameSystems.Traffic` | Inteligentní řadič křižovatek pro řízení semaforů. Podporuje semaforové fáze, vyklizovací časy a adaptivní zelenou vlnu dle aktuální hustoty dopravy. |
| [`UniversalLevelGenerator.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Infrastructure/UniversalLevelGenerator.cs) | `UniversalLevelGenerator : MonoBehaviour` | `UniversalLevelToolkit` | Procedurální generátor dungeonů a levelů. Využívá binární dělení prostoru (BSP), propojovací koridory a buněčné automaty pro organické jeskyně. |

---

## 6. /PhysicsAndCombat/ — Fyzika, balistika, aerodynamika, orbity, radary a destrukce

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`AerodynamicsAndLiftSurfaceSolver.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/AerodynamicsAndLiftSurfaceSolver.cs) | `AerodynamicsAndLiftSurfaceSolver : MonoBehaviour`, `LiftingSurface` | `GameSystems.PhysicsAndCombat` | Výpočetní aerodynamika nosných ploch. Simuluje vztlak (Lift), indukovaný odpor (Drag), úhel náběhu (AoA), pádovou rychlost (stall) a výchylky křidélek/klapek. |
| [`BallisticsAndPenetrationSolver.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/BallisticsAndPenetrationSolver.cs) | `BallisticsAndPenetrationSolver : MonoBehaviour`, `ArmorPlate` | `GameSystems.PhysicsAndCombat` | Terminální balistika a penetrace pancíře. Počítá efektivní tloušťku skloněného pancíře, mechaniku odrazů (ricochet), lámání střel a tvorbu vnitřních střepin (spalling). |
| [`DamageControlAndCompartmentSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/DamageControlAndCompartmentSim.cs) | `DamageControlAndCompartmentSim : MonoBehaviour`, `BulkheadDoor`, `HullCompartment` | `GameSystems.PhysicsAndCombat` | Systém havarijního řízení poškození trupu lodí a stanic. Modeluje zaplavování sekcí vodou, úniky atmosféry, přetlakové dveře a šíření vnitřních požárů. |
| [`GrappleController2D.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/GrappleController2D.cs) | `GrappleController2D : MonoBehaviour` | `(Global)` | Vystřelovací kotva (grappling hook) s fyzikou lana. Umožňuje zhoupnutí postavy, přitahování (reel-in) a dynamické navíjení kolem překážek. |
| [`HP.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/HP.cs) | `HP : MonoBehaviour` | `(Global)` | Správa zdraví, štítů, redukce poškození pancířem a léčení entit. Publikuje události do ZeroGCEventBus a poskytuje data pro vyhodnocování AI v UtilityBrain. |
| [`HomingProjectile.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/HomingProjectile.cs) | `HomingProjectile : MonoBehaviour` | `(Global)` | Samonaváděcí projektil a raketa. Využívá proporcionální navigaci (Proportional Navigation) pro realistické a přesné sledování manévrujícího cíle. |
| [`MarchingSquaresDestructibleTerrain.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/MarchingSquaresDestructibleTerrain.cs) | `MarchingSquaresDestructibleTerrain : MonoBehaviour`, `TerrainChunk` | `(Global)` | Destruovatelný terén generovaný v reálném čase algoritmem Marching Squares/Cubes. Umožňuje vykusování kráterů po explozích, vrtání tunelů a generování kolizí. |
| [`OrbitalMechanicsAndGravitySolver.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/OrbitalMechanicsAndGravitySolver.cs) | `OrbitalMechanicsAndGravitySolver : MonoBehaviour`, `CelestialBody`, `OrbitTrajectory` | `GameSystems.PhysicsAndCombat` | Keplerovská orbitální mechanika a N-tělesová gravitace. Počítá eliptické i hyperbolické trajektorie, Hohmannovy přechodové orbity a predikci letu. |
| [`PIDControllerAndAutopilotSuite.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/PIDControllerAndAutopilotSuite.cs) | `PIDControllerAndAutopilotSuite : MonoBehaviour`, `PID3D` | `GameSystems.PhysicsAndCombat` | Sada proporcionálně-integračně-derivačních (PID) regulátorů s ochranou proti přesycení (anti-windup). Zajišťuje stabilizaci plavidel, držení výšky a autopilot. |
| [`PortalAndNonEuclideanView2D.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/PortalAndNonEuclideanView2D.cs) | `PortalAndNonEuclideanView2D : MonoBehaviour`, `PortalTraveler`, `PortalPair` | `(Global)` | 2D portálový systém a neeukleidovský prostor. Zajišťuje bezešvý průhled portálem přes renderovací textury, zachování hybnosti a okamžitou rekurzivní teleportaci. |
| [`ProceduralIKSolver2D.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/ProceduralIKSolver2D.cs) | `ProceduralIKSolver2D : MonoBehaviour`, `JointAngularConstraint2D` | `GameSystems.Animation2D` | 2D Procedurální IK řešič. Podporuje analytický 2-Bone IK s clampingem, N-kostní iterační FABRIK pro chapadla s úhlovými limity a procedurální krokování nohou mechů. |
| [`ProjectileControl.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/ProjectileControl.cs) | `ProjectileControl : MonoBehaviour` | `(Global)` | Fyzikální balistický projektil s gravitací, odporem vzduchu, postupným úbytkem kinetické energie, průrazem materiálů a odrazy od pevných povrchů. |
| [`RadarAndEMSignatureSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/RadarAndEMSignatureSim.cs) | `RadarAndEMSignatureSim : MonoBehaviour`, `TargetEMSignature` | `GameSystems.PhysicsAndCombat` | Simulátor radaru a elektromagnetické signatury (RCS). Zahrnuje aktivní pulzní Dopplerův radar, pasivní RWR varovný přijímač a zaměřování cílů (Lock-On). |
| [`RopeAndPulleyWinchPhysics.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/RopeAndPulleyWinchPhysics.cs) | `RopeAndPulleyWinchPhysics : MonoBehaviour`, `PulleySheave` | `GameSystems.PhysicsAndCombat` | Fyzika lanových navijáků a kladkostrojů. Modeluje mechanickou výhodu kladkových soustav, dynamické tahové napětí lana a navíjení břemen jeřáby. |
| [`VerletRope.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PhysicsAndCombat/VerletRope.cs) | `VerletRope : MonoBehaviour`, `VerletNode` | `(Global)` | Fyzikální simulace lan, kabelů a visutých mostů pomocí Verletovy integrace s nastavitelným počtem iterací délkového uvolnění pro maximální stabilitu. |

---

## 7. /Automotive/ — Fyzika vozidel, motory, diferenciály, pneumatiky a telemetrie

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`ActiveElectronicsAndDriverAssists.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Automotive/ActiveElectronicsAndDriverAssists.cs) | `ActiveElectronicsAndDriverAssists : MonoBehaviour` | `(Global)` | Aktivní elektronické asistenční systémy vozidel. Zahrnuje protiblokovací brzdový systém (ABS), kontrolu trakce (TCS), elektronickou stabilizaci (ESP) a Torque Vectoring. |
| [`DrivetrainAndDifferentialSolver.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Automotive/DrivetrainAndDifferentialSolver.cs) | `DrivetrainAndDifferentialSolver : MonoBehaviour`, `DifferentialAssembly` | `(Global)` | Řešič pohonu kol a diferenciálů. Simuluje otevřené, uzamykatelné a samosvorné (LSD) diferenciály, rozdělení točivého momentu na nápravy a stupně převodovky. |
| [`InternalCombustionAndTurboSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Automotive/InternalCombustionAndTurboSim.cs) | `InternalCombustionAndTurboSim : MonoBehaviour` | `(Global)` | Termodynamická simulace spalovacího motoru. Modeluje otáčkoměr, točivý moment z křivky, prodlevu turbodmychadla (turbo lag), chlazení mezichladičem a spotřebu paliva. |
| [`MultiAxleTrailerAndHitchPhysics.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Automotive/MultiAxleTrailerAndHitchPhysics.cs) | `MultiAxleTrailerAndHitchPhysics : MonoBehaviour`, `TrailerAxle` | `(Global)` | Fyzika víconápravových návěsů a tažných zařízení. Simuluje kloubové spojení čepem, natáčení vlečených náprav a prevenci zalomení soupravy (jackknifing). |
| [`PacejkaTireAndDriftPhysics.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Automotive/PacejkaTireAndDriftPhysics.cs) | `PacejkaTireAndDriftPhysics : MonoBehaviour`, `WheelTireData` | `(Global)` | Fyzikální model pneumatik podle Pacejkovy 'Magic Formula'. Počítá podélný prokluz, úhel skluzu (slip angle), přechod ze statického tření do řízeného driftu. |
| [`RacingLineAndApexOptimizer.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Automotive/RacingLineAndApexOptimizer.cs) | `RacingLineAndApexOptimizer : MonoBehaviour`, `TrackWaypoint` | `(Global)` | Optimalizátor závodní stopy na závodním okruhu. Minimalizuje zakřivení trajektorie, hledá geometrický i pozdní apex zatáček a počítá optimální brzdné body. |
| [`SuspensionAndAeroDownforceSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Automotive/SuspensionAndAeroDownforceSim.cs) | `SuspensionAndAeroDownforceSim : MonoBehaviour`, `SuspensionStrut` | `(Global)` | Simulace pružinového odpružení a aerodynamického přítlaku. Zahrnuje tlumiče s kompresí a odskokem, příčné zkrutné stabilizátory a přítlačná křídla s ground effectem. |
| [`VehicleTelemetryAndDynoLogger.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Automotive/VehicleTelemetryAndDynoLogger.cs) | `VehicleTelemetryAndDynoLogger : MonoBehaviour`, `DynoRunRecord` | `(Global)` | Měřicí stanice a telemetrický záznamník vozidel (Dyno). Zaznamenává výkon v koních, krouticí moment, zrychlení 0-100 km/h a exportuje naměřená data do CSV. |

---

## 8. /Aviation/ — Letecké simulace, motory, fly-by-wire, dogfight a ATC

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`AircraftFuelAndCGShiftManager.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/AircraftFuelAndCGShiftManager.cs) | `AircraftFuelAndCGShiftManager : MonoBehaviour`, `AircraftFuelTank` | `(Global)` | Palivový systém letadel s dynamickým posunem těžiště (CG). Simuluje křídelní a trupové nádrže, přečerpávání paliva mezi nádržemi a nouzové odpouštění paliva. |
| [`AirTrafficControlAndRunwayGraph.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/AirTrafficControlAndRunwayGraph.cs) | `AirTrafficControlAndRunwayGraph : MonoBehaviour`, `ATCGraphNode`, `FlightStrip` | `(Global)` | Graf letištního dispečinku (ATC) a ranvejí. Řídí navádění na přistání, vyčkávací okruhy (holding patterns), pojíždění po taxidr維ách a povolení ke startu. |
| [`CountermeasuresAndEWDispenser.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/CountermeasuresAndEWDispenser.cs) | `CountermeasuresAndEWDispenser : MonoBehaviour`, `CountermeasureSalvo` | `(Global)` | Dávkovač protiopatření a elektronický boj (EW). Odpaluje tepelné klamné cíle (flares) a dipólové odražeče (chaff) pro zmatení radarových a infračervených střel. |
| [`DogfightAIAndBFMController.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/DogfightAIAndBFMController.cs) | `DogfightAIAndBFMController : MonoBehaviour` | `(Global)` | Taktická umělá inteligence pro letecký souboj (Dogfight). Provádí manévry základního stíhacího boje (BFM): Immelmann, Split-S, High/Low Yo-Yo a nůžky (scissors). |
| [`FireControlAndLeadComputingSight.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/FireControlAndLeadComputingSight.cs) | `FireControlAndLeadComputingSight : MonoBehaviour` | `(Global)` | Systém řízení palby a gyroskopický zaměřovač s předsazením (LCOS). Počítá bod setkání střel s manévrujícím cílem na základě vzdálenosti, balistiky a rychlosti. |
| [`FlyByWireAndFlightEnvelopeProtection.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/FlyByWireAndFlightEnvelopeProtection.cs) | `FlyByWireAndFlightEnvelopeProtection : MonoBehaviour` | `(Global)` | Elektronické řízení letu (Fly-By-Wire) s obálkou letové ochrany. Omezuje maximální přetížení G-load, brání překročení kritického úhlu náběhu a pádu do vývrtky. |
| [`GFatigueAndPilotPhysiologySim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/GFatigueAndPilotPhysiologySim.cs) | `GFatigueAndPilotPhysiologySim : MonoBehaviour` | `(Global)` | Fyziologická simulace pilota vystaveného přetížení. Simuluje zúžení zorného pole (tunnel vision), šedé vidění (greyout), ztrátu vědomí (G-LOC) a červené vidění (redout). |
| [`ILSAndCarrierLandingSystem.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/ILSAndCarrierLandingSystem.cs) | `ILSAndCarrierLandingSystem : MonoBehaviour`, `ArrestorWire` | `(Global)` | Přístrojový systém přistání (ILS) a palubní záchytný systém letadlových lodí. Poskytuje naváděcí paprsky sestupové roviny (glideslope/localizer) a zachycení lanem. |
| [`JetAndPropellerEngineSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Aviation/JetAndPropellerEngineSim.cs) | `JetAndPropellerEngineSim : MonoBehaviour` | `(Global)` | Simulace leteckých proudových motorů a vrtulí. Modeluje tah dmychadla, režim přídavného spalování (afterburner), pokles hustoty vzduchu s výškou a stoupání vrtule. |

---

## 9. /Cybernetics/ — Logické obvody, virtuální VM počítače a sítě

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`CryptographyAndSignalDecoderSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Cybernetics/CryptographyAndSignalDecoderSim.cs) | `CryptographyAndSignalDecoderSim : MonoBehaviour`, `RadioSignalPacket` | `GameSystems.Cybernetics` | Kryptografie a dekodér rádiového spektra. Zahrnuje frekvenční analýzu šifer, XOR maskování, vodopádový rádiový spektrogram a demodulaci signálů. |
| [`InGameProgrammableVM.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Cybernetics/InGameProgrammableVM.cs) | `InGameProgrammableVM : MonoBehaviour`, `VirtualCPU`, `OpCode` | `GameSystems.Cybernetics` | Herní programovatelný virtuální počítač (VM). Obsahuje virtuální registry, aritmeticko-logickou jednotku (ALU), zásobník a interpreter assembleru. |
| [`LogicGateAndCircuitSimulator.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Cybernetics/LogicGateAndCircuitSimulator.cs) | `LogicGateAndCircuitSimulator : MonoBehaviour`, `LogicNode`, `CircuitWire` | `GameSystems.Cybernetics` | Simulátor digitálních logických obvodů. Modeluje logická hradla AND, OR, NOT, XOR, klopné obvody (Flip-Flops), multiplexery, sběrnice a hodinové impulsy. |
| [`VirtualNetworkAndHackingSim.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Cybernetics/VirtualNetworkAndHackingSim.cs) | `VirtualNetworkAndHackingSim : MonoBehaviour`, `VirtualHost`, `NetworkPacket` | `GameSystems.Cybernetics` | Simulovaná počítačová síť a hackerská konzole. Umožňuje skenování otevřených portů, prolamování firewallů, směrování paketů a spouštění terminálových exploitů. |

---

## 10. /PlayerAndControls/ — Ovládání hráče, vstupy, statistiky a kamera

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`CameraController.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PlayerAndControls/CameraController.cs) | `CameraController : MonoBehaviour` | `(Global)` | Plynulé sledování cíle kamerou (Smooth Follow) s nastavitelnou mrtvou zónou pohledu, ohraničením herní scény (clamping) a dynamickým zoomem. |
| [`InputManager.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PlayerAndControls/InputManager.cs) | `InputManager : MonoBehaviour` | `(Global)` | Centrální wrapper a přístupový bod pro Unity New Input System. Sjednocuje čtení klávesnice, myši i herních ovladačů. |
| [`Movement.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PlayerAndControls/Movement.cs) | `Movement : MonoBehaviour` | `(Global)` | 2D fyzikální pohyb postavy a vozidla s plynulým zrychlením (throttle), rotací a strafováním přes Rigidbody2D. |
| [`PlayerControls.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PlayerAndControls/PlayerControls.cs) | `PlayerControls : IInputActionCollection2`, `IDisposable` | `(Global)` | Automaticky generovaný C# kód mapování akcí pro New Input System (Move, Look, Fire, Interact). |
| [`PlayerStats.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PlayerAndControls/PlayerStats.cs) | `PlayerStats : MonoBehaviour` | `(Global)` | Správa atributů hráče: zdraví, mana/energie, sběr zkušeností (XP), úrovně (leveling) a škálování statistik. |

---

## 11. /Core/ — Architektura, event bus, inventář, procedurální generátory a inspektor

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`CommandBufferUndoSystem.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/CommandBufferUndoSystem.cs) | `CommandBufferUndoSystem : MonoBehaviour`, `IGameCommand`, `MacroCommand` | `GameSystems.Commands` | Undo/Redo systém založený na návrhovém vzoru Command. Podporuje atomické makro-příkazy pro hromadné operace (např. stavba silnice o 20 polích). |
| [`DeterministicPRNGAndNoiseLibrary.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/DeterministicPRNGAndNoiseLibrary.cs) | `DeterministicPRNGAndNoiseLibrary`, `FastNoiseCore`, `Xoshiro256Core` | `(Global)` | Deterministický pseudonáhodný generátor čísel (Xoshiro256, PCG) a knihovna koherentního šumu (Simplex, Worley, Perlin, Fractal Brownian Motion). |
| [`LogisticsJobSystem.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/LogisticsJobSystem.cs) | `LogisticsJobSystem : MonoBehaviour`, `LogisticsJob`, `ILogisticsWorker` | `GameSystems.Logistics` | Asynchronní logistický systém přepravních zakázek. Řídí nosiče, transportní drony a nákladní vozy při přepravě surovin s prioritami a rezervacemi. |
| [`MarkovNameAndLoreGenerator.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/MarkovNameAndLoreGenerator.cs) | `MarkovNameAndLoreGenerator`, `MarkovModel` | `(Global)` | Generátor procedurálních jmen, názvů frakcí a textového lore pomocí N-gramových Markovových řetězců s vestavěnými slovníky (Fantasy, Sci-Fi, Severské). |
| [`ObjectPoolManager.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/ObjectPoolManager.cs) | `ObjectPoolManager : MonoBehaviour`, `Pool` | `(Global)` | Generický pooling herních objektů a komponent s nulovou alokací na GC pro projektily, částicové efekty a zvukové přehrávače. |
| [`SaveLoadSerializer.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/SaveLoadSerializer.cs) | `SaveLoadSerializer : MonoBehaviour`, `ISaveableSystem`, `SaveMetadata` | `GameSystems.Persistence` | Univerzální ukládací a načítací systém. Snímá stavy všech komponent implementujících ISaveableSystem do atomického JSON/binárního souboru s kontrolním součtem SHA-256. |
| [`TimeRewindSystem.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/TimeRewindSystem.cs) | `TimeRewindSystem : MonoBehaviour`, `PointInTime` | `(Global)` | Systém přetáčení času ve stylu hry Braid. Ukládá pozice, rotace a stavy do kruhového bufferu a umožňuje plynulý zpětný chod času. |
| [`UniversalInventoryContainer.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/UniversalInventoryContainer.cs) | `UniversalInventoryContainer : MonoBehaviour`, `InventoryItem`, `StatBonus` | `GameSystems.Inventory` | Univerzální inventář a sklad. Podporuje stohování předmětů, nosnost/váhu, stupně vzácnosti, bonusy ke statistikám, integraci událostí a ukládání. |
| [`VisualInspectorAttributes.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/VisualInspectorAttributes.cs) | `ButtonAttribute`, `ShowIfAttribute`, `HideIfAttribute`, `ReadOnlyInspectorAttribute`, `ProgressBarAttribute`, `MinMaxSliderAttribute`, `BoxGroupAttribute`, `TabGroupAttribute`, `RequiredFieldAttribute`, `TelemetryGraphAttribute` | `VisualInspectorStudio` | Sada vlastních atributů pro tvorbu moderního uživatelského rozhraní v inspektoru Unity (inspirováno balíčkem Odin Inspector) bez závislostí třetích stran. |
| [`ZeroGCEventBus.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Core/ZeroGCEventBus.cs) | `ZeroGCEventBus`, `EventBus<T>`, `IGameEvent`, `EventCallback<T>` | `GameSystems.Events` | Ultrarychlá generická statická sběrnice událostí s nulovou alokací paměti. Předává parametry přes 'in struct', podporuje bezpečné přihlášení a automatický reset v Play módu. |

---

## 12. /AudioAndVFX/ — Vizuální efekty, mlha války, syntéza zvuku a rytmika

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`FogOfWarManager.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AudioAndVFX/FogOfWarManager.cs) | `FogOfWarManager : MonoBehaviour` | `GameSystems.Vision` | Dynamická mlha války (Fog of War) s pamětí prozkoumaných oblastí. Využívá 2D raycasting zorného pole a dynamickou texturu promítanou na herní scénu. |
| [`ProceduralAudioSynthesizer.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AudioAndVFX/ProceduralAudioSynthesizer.cs) | `ProceduralAudioSynthesizer : MonoBehaviour`, `SynthVoice`, `ADSREnvelope` | `(Global)` | Procedurální DSP zvukový syntezátor běžící v reálném čase. Obsahuje oscilátory (Sinus, Čtverec, Pila, Bílý šum), ADSR obálky, filtry a generátor laserů i explozí. |
| [`RhythmConductor.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AudioAndVFX/RhythmConductor.cs) | `RhythmConductor : MonoBehaviour`, `Judgement` | `(Global)` | Rytmický dirigent synchronizovaný s AudioSettings.dspTime. Počítá beaty, taktové míry a vyhodnocuje přesnost stisku (Perfect, Good, OK, Miss). |
| [`SpeechToTextManager.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AudioAndVFX/SpeechToTextManager.cs) | `SpeechToTextManager : MonoBehaviour`, `ISpeechToTextProvider` | `GameSystems.Speech` | Záznam mikrofonu s Voice Activity Detection (VAD), WAV enkódováním v paměti a lokálním parserem hlasových povelů pro hlasové ovládání herních akcí. |
| [`SpriteFlasher.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AudioAndVFX/SpriteFlasher.cs) | `SpriteFlasher : MonoBehaviour` | `(Global)` | Krátký barevný záblesk spritu (Damage Flash) při zásahu zbraní s využitím instancovaných materiálových vlastností bez duplikování materiálů. |
| [`VisualEffectManager.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/AudioAndVFX/VisualEffectManager.cs) | `VisualEffectManager : MonoBehaviour` | `(Global)` | Centrální správce vizuálních efektů: poolované částicové systémy, otisky střel (decals), otřesy obrazovky (screen shake) a plovoucí čísla poškození (damage numbers). |

---

## 13. /Assets/Editor/ — UI Toolkit nástroje: EditorProWorkspace & VisualInspectorStudio

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`EditorProAssetDatabaseCache.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Assets/Editor/EditorProWorkspace/EditorProAssetDatabaseCache.cs) | `EditorProAssetDatabaseCache`, `ScriptableObjectTypeInfo` | `EditorProWorkspace` | Automatické vyhledávání typů pomocí TypeCache, indexování aktiv na disku, vyhledávací engine (t:Type, -path:, fuzzy) a AssetPostprocessor pro auto-refresh. |
| [`EditorProDataAndSettings.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Assets/Editor/EditorProWorkspace/EditorProDataAndSettings.cs) | `EditorProCategoryAttribute`, `IEditorProBadgeProvider` | `EditorProWorkspace` | Datové struktury, ScriptableSingleton perzistence v ProjectSettings/EditorProSettings.json, kategorie, oblíbené položky, štítky a konfigurace workspace. |
| [`EditorProWindow.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Assets/Editor/EditorProWorkspace/EditorProWindow.cs) | `EditorProWindow : EditorWindow` | `EditorProWorkspace` | Třípanelové responsivní okno Unity 6 UI Toolkit s TwoPaneSplitView, mřížkovým (Dense Grid) a tabulkovým zobrazením, záložkami inspektoru a tvorbou aktiv. |
| [`VisualInspectorEditor.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/Assets/Editor/VisualInspectorStudio/VisualInspectorEditor.cs) | `VisualInspectorMonoBehaviourEditor`, `VisualInspectorScriptableObjectEditor`, `VisualInspectorEditorBase` | `VisualInspectorStudio.Editor` | UI Toolkit univerzální vlastní inspektor (isFallback = true) pro MonoBehaviour a ScriptableObject. Vykresluje skupiny BoxGroup/TabGroup, podmínky ShowIf/HideIf, tlačítka metod a telemetrické grafy. |

---

## 14. /PiperTTS/ — Lokální offline neurální syntéza řeči Piper (Text-to-Speech)

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`ESpeakNative.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PiperTTS/Assets/Piper/Scripts/ESpeakNative.cs) | `ESpeakNative` | `Piper` | Nativní P/Invoke rozhraní pro vestavěnou knihovnu eSpeak-ng sloužící k fonetickému přepisu textových řetězců do fonémů. |
| [`PiperDemoSceneBuilder.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PiperTTS/Assets/Piper/Editor/PiperDemoSceneBuilder.cs) | `PiperDemoSceneBuilder` | `Piper.EditorTools` | Editorový nástroj pro automatické vygenerování a nastavení demonstrační testovací scény pro syntézu hlasu. |
| [`PiperDemoUI.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PiperTTS/Assets/Piper/Demo/PiperDemoUI.cs) | `PiperDemoUI : MonoBehaviour` | `Piper` | Uživatelské rozhraní pro testování syntézy řeči v reálném čase, výběr hlasových modelů a nastavování parametrů tempa. |
| [`PiperTTS.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PiperTTS/Assets/Piper/Scripts/PiperTTS.cs) | `PiperTTS : MonoBehaviour` | `Piper` | Hlavní řídicí komponenta neurální syntézy řeči. Využívá ONNX runtime pro převod fonémů na audio vzorky s přehráváním přes AudioSource. |
| [`PiperVoiceConfig.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PiperTTS/Assets/Piper/Scripts/PiperVoiceConfig.cs) | `PiperVoiceConfig` | `Piper` | Konfigurační model hlasu, načítání metadat z JSON profilů hlasových modelů a mapování fonémových tabulek. |
| [`PiperVoiceDownloader.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PiperTTS/Assets/Piper/Editor/PiperVoiceDownloader.cs) | `PiperVoiceDownloader : EditorWindow` | `Piper.EditorTools` | Editorové okno pro stahování a instalaci oficiálních vícejazyčných ONNX modelů Piper z repozitářů HuggingFace. |
| [`VoiceLines.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PiperTTS/Assets/Piper/Scripts/VoiceLines.cs) | `VoiceLines : MonoBehaviour` | `Piper` | Správce předpřipravených dialogových replik a fronty přehrávání hlasů postav s podporou lokalizace. |

---

## 15. /PixelLight2D/ — 2D pixel-artové dynamické osvětlení

| Soubor | Hlavní třídy | Namespace | Popis |
| :--- | :--- | :--- | :--- |
| [`BreathLight2D.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PixelLight2D/BreathLight2D.cs) | `BreathLight2D : MonoBehaviour` | `(Global)` | Plynulá sinusová pulzace a animace dýchání 2D bodových a plošných světel pro magické krystaly, lucerny a pochodně. |
| [`PixelLightAnimator.cs`](file:///c:/Users/ilike/Documents/GitHub/unity%20c%23%20code/PixelLight2D/PixelLightAnimator.cs) | `PixelLightAnimator : MonoBehaviour` | `(Global)` | Dynamický animátor pixel-artových světel. Řídí flicker efekty, změny barevných palet a vrhání stínů na 2D normálové mapy. |

---

## 🛠️ Verifikace sestavení
Všechny skripty jsou verifikovány příkazem:
```bash
dotnet build .validation/Validator.csproj --no-incremental
```
Stav: **0 chyb, 0 varování (107/107 skriptů úspěšně sestaveno)**.

