# Autonomous Coding Agent – Sequential Implementation Plan (`unity-c--code`)

## 1. Role & Execution Protocol (CRITICAL – READ FIRST)

You are an autonomous Senior Unity C# Systems Architect working on the `unity-c--code` repository. Your objective is to implement a collection of 22 standalone, production-ready, high-performance C# scripts for strategy games, city builders, simulations, and tactical AI.

### Strict Sequential Workflow Rules

1. **NO PARALLEL EXECUTION:** You MUST work strictly sequentially (one file at a time, from Task 01 to Task 22). Never attempt to generate, edit, or Stub multiple scripts at once.

2. **NO PLACEHOLDERS OR TODOs:** Every script must be 100% complete, compilable, and self-contained before moving to the next task. Do not leave `// TODO` comments or empty method bodies.

3. **STEP-BY-STEP LOOP FOR EACH TASK:**

   * **Step A:** Read the next unchecked `[ ]` task in the Master Task Queue below.

   * **Step B:** Design and implement the complete `.cs` file in the root directory (or appropriate subfolder if specified) adhering to all Architectural Standards.

   * **Step C:** Perform a static self-review (check for missing `using` directives, GC allocations in hot loops, null safety, and Inspector serialization).

   * **Step D:** Update this `AGENT_TASKS.md` file by changing `[ ]` to `[x]` for the completed task.

   * **Step E:** Proceed immediately to the next unchecked task.

## 2. Global Architectural & Coding Standards

Every single C# script you generate MUST obey the following rules:

* **Zero-GC in Hot Paths:** Avoid LINQ (`System.Linq`), closures, `foreach` over non-list collections, or array allocations inside `Update()`, `FixedUpdate()`, `LateUpdate()`, or simulation ticks. Pre-allocate buffers, arrays, and `List<T>` capacities in `Awake()`.

* **Standalone & Decoupled:** Scripts must compile without requiring paid third-party assets. When integrating with existing repo scripts (e.g., `FormationBrain.cs`, `AIPerception.cs`, `AStarPathfinder.cs`, `PiperTTS`), use clean C# interfaces, events (`Action<T>`), or optional component references so each script remains modular and reusable.

* **Inspector & Debugging Friendly:** Use `[Header]`, `[Tooltip]`, `[SerializeField]`, and `[Range]` attributes. Include `OnDrawGizmosSelected()` or `OnDrawGizmos()` to visualize grids, vectors, paths, blocks, and radii directly in the Unity Scene View.

* **Clean C# Documentation:** Add concise XML documentation (`/// <summary>`) to public classes, interfaces, and core API methods.

## 3. Master Sequential Task Queue

### Phase 1: Economy & Core Simulation (Pre-verified / Foundation)

* \[x\] **Task 01: `DynamicMarketEconomy.cs`** *(Already provided – verify file exists in repo or write from specification)*

  * **Scope:** Generic, extensible market simulation supporting both physical resources and financial instruments (stocks, bonds, custom assets).

  * **Requirements:**

    * Define `ITradableAsset` interface (`AssetId`, `DisplayName`, `BaseValue`, `MinValue`, `MaxValue`, `Volatility`).

    * Define `IPricingStrategy<T> where T : ITradableAsset` for swappable price calculation formulas.

    * Implement `PhysicalResourceAsset` (`ScriptableObject`) covering raw materials, refined materials, commodities, weight, and spoilage rates.

    * Implement `StockAsset` (`[Serializable]` class) covering ticker, issued shares, net worth, quarterly profit, dividend yield, and market sentiment.

    * Implement `MarketListing<T>` with a pre-allocated ring buffer (`float[]`) for zero-GC historical price tracking (for charting).

    * Implement generic `MarketPool<T>` managing supply, demand, equilibrium decay, buy/sell execution with taxes/fees, and `DynamicMarketEconomy` (`MonoBehaviour`) capable of registering unlimited custom `MarketPool<T>` sectors at runtime.

* [x] **Task 02: `TickSimulationManager.cs`**

  * **Scope:** Deterministic-style fixed-step simulation clock decoupled from frame rate, featuring time-slicing across frames to handle 10,000+ entities without FPS drops.

  * **Requirements:**

    * Define `ITickable` interface (`void OnSimulationTick(int tickIndex, float tickDeltaTime)`).

    * Support multiple tick frequencies (e.g., Fast Tick: 5x/sec, Economy Tick: 1x/sec, Macro Tick: 1x/10sec).

    * Implement a time-sliced execution queue (`RegisterTickable`, `UnregisterTickable`) that distributes registered `ITickable` updates evenly across frames using a configurable budget (`maxUpdatesPerFrame` or millisecond budget via `Stopwatch`).

    * Include simulation speed controls (Pause, 1x, 2x, 5x, 10x) and single-step debugging.

* [x] **Task 03: `CellularAutomataSim.cs`**

  * **Scope:** High-speed 2D double-buffered grid simulation for fire propagation, liquid/water flow, pollution/smog diffusion, and forest growth.

  * **Requirements:**

    * Use flat 1D arrays (`CellState[] currentGrid`, `CellState[] nextGrid`) indexed by `y * width + x` for cache locality and zero GC.

    * Support cell attributes: `State` (Empty, Flammable, Burning, Burnt, Water, Wall), `Fuel`, `Moisture`, `LiquidLevel`, and `PollutionLevel`.

    * Implement configurable neighborhood rules (Moore 8-neighbor and Von Neumann 4-neighbor) plus wind direction/speed bias affecting fire and smog spread.

    * Expose public API (`IgniteCell`, `AddLiquid`, `AddPollution`, `SetObstacle`, `StepSimulation`) and optional Texture2D / Gizmo visualization.

### Phase 2: Spatial Partitioning, Mass AI & Tactical Awareness

* [x] **Task 04: `SpatialHashGrid.cs`**

  * **Scope:** Ultra-fast 2D/3D spatial indexing structure replacing expensive `Physics2D.OverlapCircle` calls for `FlockingBrain.cs`, `AIPerception.cs`, and `EcosystemEvolutionSim.cs`.

  * **Requirements:**

    * Implement generic `SpatialHashGrid<T>` where entities register with a position and radius/bounds.

    * Use pre-allocated cell buckets (`Dictionary<long, List<T>>` with object-pooled lists or flat array buckets) to avoid allocations when entities move (`UpdateEntityPosition`).

    * Provide zero-allocation query methods: `QueryRadius(Vector3 center, float radius, List<T> resultsBuffer)`, `QueryAABB(...)`, and `FindNearest(Vector3 center, float maxRadius, Predicate<T> filter)`.

* [x] **Task 05: `ContextSteering.cs`**

  * **Scope:** Local obstacle avoidance and target seeking using Interest and Danger directional weight maps (8, 16, or 32 discrete ray directions).

  * **Requirements:**

    * Maintain `float[] interestMap` and `float[] dangerMap` per agent.

    * Provide modular evaluators: Seek target, Flee threat, Avoid obstacles (via raycasts or `SpatialHashGrid` queries), and Maintain alignment/cohesion.

    * Mask out directions where danger exceeds a threshold, interpolate between remaining top interest directions for smooth sub-angle movement, and output a smoothed steering velocity vector.

    * Draw clear Scene View gizmos (green lines for interest, red lines for danger, yellow arrow for final chosen vector).

* [x] **Task 06: `HierarchicalStateMachine.cs`**

  * **Scope:** Clean, reusable Hierarchical Finite State Machine (HFSM) supporting nested sub-states, guard conditions, and priority transitions.

  * **Requirements:**

    * Implement generic `State<TContext>` and `StateMachine<TContext>` classes (no MonoBehaviour overhead per state).

    * Support hierarchical nesting: parent states execute `OnUpdate` and propagate down to active child states; transitions at a parent level cleanly exit active child sub-states first.

    * Support transition triggers, boolean/predicate transitions (`AddTransition(from, to, condition)`), any-state transitions, and state history (revert to previous state).

* [x] **Task 07: `FlowFieldPathfinder.cs`**

  * **Scope:** Vector field / Dijkstra grid pathfinder capable of guiding thousands of units toward a shared destination with $O(1)$ per-agent lookup cost.

  * **Requirements:**

    * Maintain three grids: `byte[] costField` (1 = walkable, 2–254 = rough terrain, 255 = impassable), `ushort[] integrationField` (total cost to goal), and `Vector2[] flowField` (normalized direction vectors).

    * Support multi-goal targets (e.g., pathing toward any tile of a large building or border) and dynamic obstacle stamping.

    * Provide bilinear interpolation sampling (`SampleFlowDirection(Vector3 worldPos)`) so units move smoothly across cell boundaries.

* [x] **Task 08: `InfluenceMap.cs`**

  * **Scope:** Multi-layer 2D spatial grid for tactical AI reasoning, crime/pollution tracking, land value calculation, and territory control.

  * **Requirements:**

    * Support multiple named or enum-indexed layers (`InfluenceLayer`) with configurable resolution, decay rates, and diffusion/blur speeds (momentum + linear/exponential falloff).

    * Allow stamping positive or negative influence emitters (`StampInfluence(Vector3 worldPos, float value, float radius, FalloffCurveType)`).

    * Include layer math operations: weighted sum, subtraction, multiplication, and threshold queries (`FindHighestInfluenceCell`, `FindSafestCellInRadius`).

* [x] **Task 09: `RVO2Simulator.cs`**

  * **Scope:** Reciprocal Velocity Obstacles (ORCA / RVO2 principles) crowd simulator preventing agents from clipping or deadlocking in narrow corridors.

  * **Requirements:**

    * Manage registered crowd agents (`RVOAgentData`: Position, CurrentVelocity, PreferredVelocity, Radius, MaxSpeed, TimeHorizon, ResponsibilityWeight).

    * Compute velocity constraints between neighboring agents so each agent takes proportional responsibility (e.g., 50/50) for avoiding predicted collisions within `TimeHorizon`.

    * Support static polygon/segment obstacles and stationary agents (immovable priority).

* [x] **Task 10: `FogOfWarManager.cs`**

  * **Scope:** High-performance grid and texture-based visibility manager supporting 3 states: Unexplored (Black), Explored/Shroud (Dimmed), and Currently Visible (Clear).

  * **Requirements:**

    * Track vision emitters (units/buildings with position, sight radius, and optional height advantage).

    * Implement a fast 2D Shadowcasting (Bresenham / Recursive Octant Shadowcasting) algorithm that respects vision-blocking cells on the grid.

    * Output visibility data to a dynamically updated `Texture2D` (`Color32[]` buffer with smooth temporal interpolation between ticks) ready to bind to any screen-space or world-space shader, plus `IsWorldPositionVisible(Vector3 pos)` API for AI/UI culling.

### Phase 3: City Building, Grids, Roads & Utility Networks

* [x] **Task 11: `GridPlacementSystem.cs`**

  * **Scope:** Complete grid building system with snapping, 90° rotation, multi-cell footprints (e.g., 3x2, 4x4), terrain/collision validation, and ghost preview.

  * **Requirements:**

    * Define `PlaceableBuildingSO` (`ScriptableObject` or serializable data) with footprint dimensions (`Vector2Int`), custom occupancy mask (for L-shaped buildings), build cost, and prefab references.

    * Calculate rotated footprint coordinates accurately for 0°, 90°, 180°, and 270° orientations.

    * Manage a grid occupancy map storing cell state and reference to the placed building instance (so demolishing a 3x2 building frees all 6 cells).

    * Control a visual Ghost preview object that tints green/red based on placement validity (`CanPlaceBuilding`).

* [x] **Task 12: `AutoTileBitmasker.cs`**

  * **Scope:** Automatic 4-bit (Cardinal, 16 variants) and 8-bit (Blob/Corner, 47/256 variants) bitmask calculator that updates neighbor sprites or 3D meshes when placing walls, pipes, or roads.

  * **Requirements:**

    * Compute bitmask values from orthogonal and diagonal neighbors matching a target tile category/ID.

    * Provide lookup tables mapping raw 8-bit masks (with corner masking so irrelevant diagonal bits are ignored when orthogonal neighbors are missing) to compact tile indices.

    * Automatically trigger cascading updates strictly on the placed/removed cell and its 8 immediate neighbors.

* [x] **Task 13: `SplineRoadBuilder.cs`**

  * **Scope:** Procedural Cubic Bézier / Catmull-Rom spline builder for roads, railways, and conveyor belts, featuring automatic node snapping and intersection creation.

  * **Requirements:**

    * Maintain a graph of `RoadNode` (intersections/endpoints) and `RoadSegment` (spline curves with control points, width, and lane count).

    * Implement arc-length parameterization (lookup table of distances along the curve) so vehicles move at constant speed regardless of control point spacing.

    * Support splitting an existing `RoadSegment` into two segments and inserting a new `RoadNode` (T-junction or 4-way intersection) when the player snaps a new road onto the middle of an existing road.

    * Generate procedural mesh data (`Mesh` with vertices, UVs, triangles) or spaced prefabs along the spline.

* [x] **Task 14: `TrafficIntersectionController.cs`**

  * **Scope:** Traffic management system connected to `SplineRoadBuilder.cs` handling traffic lights, priority rules (yield/stop), and intersection reservation to prevent gridlock.

  * **Requirements:**

    * Support 3 intersection modes per `RoadNode`: Uncontrolled/Priority (Right-of-way), Stop Sign (FIFO queue), and Timed Traffic Light Phases (Green/Yellow/Red phase cycles).

    * Implement Conflict Zone reservation: vehicles approaching an intersection check if their entry-to-exit trajectory crosses another reserved path and also verify that the exit lane has free space (anti-gridlock check).

    * Include a lightweight `TrafficVehicleAgent` controller that follows spline lanes and maintains safe braking distance (`Intelligent Driver Model` / IDM spacing) behind the vehicle ahead.

* [x] **Task 15: `RailBlockSignalingSystem.cs`**

  * **Scope:** Factorio / OpenTTD-style railway block signaling system dividing track graphs into discrete collision-free blocks with Regular Signals and Chain Signals.

  * **Requirements:**

    * Partition connected track segments into discrete `RailBlock` instances bounded by signals (`RailSignal`).

    * Track block occupancy (`Free`, `Reserved`, `Occupied`) based on train bounding boxes/carriages.

    * Implement **Regular Signals** (Green if the immediate next block is free, Yellow if reserved, Red if occupied) and **Chain Signals** (look ahead along the train's requested path; only turn Green if both the intersection block AND the exit block beyond it are free, preventing trains from stopping inside intersections).

* [x] **Task 16: `ResourceNetworkGraph.cs`**

  * **Scope:** Graph-based utility network solver for electricity, water, heating, or data grids, calculating connected components, production vs. consumption, storage batteries/tanks, and brownouts.

  * **Requirements:**

    * Represent nodes as Producers, Consumers, Storage (accumulators/tanks), and Conductors/Switches (power poles, pipes, substations).

    * Rebuild Connected Components (`SubGrid`) using fast non-recursive BFS/Union-Find only when graph topology changes (node/edge added, destroyed, or switched off).

    * Every simulation tick, calculate total supply, demand, and storage charge/discharge per `SubGrid`, outputting a `SatisfactionRatio` ($0.0$ to $1.0$) to all connected consumers.

### Phase 4: Logistics, Factory Chains, Selection & Architecture

* [x] **Task 17: `LogisticsJobSystem.cs`**

  * **Scope:** RimWorld / Factorio / Workers & Resources task queue and hauling dispatcher matching supply/demand transport requests with idle workers, trucks, or drones.

  * **Requirements:**

    * Track `LogisticsRequester` (needs X units of Resource A, priority 1–5), `LogisticsProvider` (has Y units of Resource A available), and `LogisticsJob` (`Source`, `Destination`, `ResourceId`, `Amount`, `Priority`).

    * Implement reservation tracking (`ReservedIncoming`, `ReservedOutgoing`) so 10 idle trucks don't all drive to pick up the same single crate of iron.

    * Score and assign pending jobs to available `ILogisticsWorker` agents based on job priority, carrying capacity, and distance/travel cost, including graceful job cancellation/re-queueing if a worker is destroyed mid-delivery.

* [x] **Task 18: `ProductionChainNode.cs`**

  * **Scope:** Universal factory, refinery, mine, and warehouse logic managing recipes, input/output inventories, worker efficiency, power satisfaction, and automatic logistics hooks.

  * **Requirements:**

    * Define `ProductionRecipeSO` (`ScriptableObject`) with multiple input ingredients (`ResourceId + Amount`), multiple outputs (including byproducts like pollution/slag), crafting time, and power/worker requirements.

    * Manage clamped Input Buffer and Output Buffer inventories.

    * Scale production speed dynamically by current workforce ratio and `ResourceNetworkGraph` power/water satisfaction.

    * Automatically emit supply/demand thresholds to `LogisticsJobSystem` or `DynamicMarketEconomy` when input buffers drop below a threshold or output buffers fill up.

* [x] **Task 19: `RTSSelectionAndGroupManager.cs`**

  * **Scope:** Complete RTS unit/building selection controller supporting click selection, screen-space marquee box selection, double-click same-type selection, control groups (`Ctrl + 1..9`), and command delegation.

  * **Requirements:**

    * Define `ISelectable` interface (`Transform`, `SelectionBounds`, `TypeId`, `OnSelected()`, `OnDeselected()`).

    * Implement screen-space marquee box selection (`SelectionRect`) with shift-add and ctrl-toggle modifiers.

    * Implement double-click selection (selects all visible units with matching `TypeId` on screen) and control group binding (`Ctrl + 0..9` to save, `0..9` to recall, double-tap `0..9` to center camera).

    * Provide command dispatch callbacks (`OnIssueMoveCommand(Vector3 targetPos)`) ready to feed target coordinates into `FormationBrain.cs` or `FlowFieldPathfinder.cs`.

* [x] **Task 20: `CommandBufferUndoSystem.cs`**

  * **Scope:** Command Pattern execution queue with full Undo/Redo history stack, batch macro commands, and optional economic cost refunds.

  * **Requirements:**

    * Define `IGameCommand` interface (`bool CanExecute()`, `void Execute()`, `void Undo()`, `string Description`).

    * Implement `CompositeMacroCommand` so multi-tile actions (e.g., dragging a 20-tile road or demolishing an entire city block) undo/redo as a single atomic step.

    * Maintain a bounded history ring/stack (`maxUndoSteps = 64`), clearing the redo stack upon new command execution, and firing events for UI updates.

* [x] **Task 21: `SaveLoadSerializer.cs`**

  * **Scope:** Universal, modular save/load system capable of capturing and restoring state across all managers (grid buildings, factory buffers, market prices, agents) into JSON or binary files with integrity checks.

  * **Requirements:**

    * Define `ISaveableSystem` interface (`string SaveKey { get; }`, `object CaptureState()`, `void RestoreState(string stateJson)`).

    * Automatically discover or allow registration of all active `ISaveableSystem` components in the scene.

    * Package all system snapshots with metadata (Save Version, Timestamp, Playtime, SHA-256 checksum) into a single structured save file on disk using atomic file write (write to `.tmp`, then replace target file so a crash mid-save never corrupts existing saves).

    * Support both human-readable JSON (for debugging) and compressed/obfuscated binary output.

* [x] **Task 22: `SpeechToTextManager.cs`**

  * **Scope:** Local microphone voice capture with Voice Activity Detection (VAD), WAV encoding in memory, and modular Speech-to-Text transcription backend (compatible with local Whisper.unity / ONNX runtime or local HTTP server, acting as the input counterpart to `PiperTTS`).

  * **Requirements:**

    * Manage Unity `Microphone` recording into a circular `AudioClip` buffer with configurable sample rate (16 kHz default for Whisper).

    * Implement real-time RMS / energy-based Voice Activity Detection (VAD) with configurable silence duration threshold (automatically starts capturing when the player speaks and triggers transcription when they stop speaking, plus Push-to-Talk mode).

    * Convert captured float PCM samples into a clean 16-bit PCM WAV `byte[]` array in memory without disk I/O overhead.

    * Define `ISpeechToTextProvider` interface (`Task<string> TranscribeAsync(byte[] wavData, float[] rawSamples, string language)`) with a built-in Local HTTP/Whisper API provider AND a Local Keyword/Command Parser that maps recognized text (e.g., *"postav elektrárnu"*, *"útok na sektor B"*) directly to game events.