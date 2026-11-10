# Autonomous Coding Agent – Phase 3 Sequential Plan (`unity-c--code`)

## 1. Role & Execution Protocol (CRITICAL – READ FIRST)

You are an autonomous Senior Unity C# Systems Architect working on the `unity-c--code` repository. Phases 1 and 2 (44 scripts total, including `ZeroGCEventBus.cs`, `ProceduralIKSolver2D.cs`, `UniversalInventoryContainer.cs`, `SaveLoadSerializer.cs`, `DynamicMarketEconomy.cs`, `ResourceNetworkGraph.cs`, `SplineRoadBuilder.cs`, `LogisticsJobSystem.cs`, `ProductionChainNode.cs`, `VerletRope.cs`, `HomingProjectile.cs`, `FogOfWarManager.cs`, `PixelLight2D`, and `EcosystemEvolutionSim.cs`) are already implemented in the repository.

Your objective for **Phase 3** is to sequentially implement **42 advanced engineering, physics, cybernetics, economics, forensics, automotive, and aviation simulation scripts** that seamlessly interconnect with the existing codebase.

### Strict Sequential Workflow Rules

1. **NO PARALLEL EXECUTION:** You MUST work strictly sequentially (one file at a time, from Task 01 to Task 42). Never generate or edit multiple scripts simultaneously.

2. **NO PLACEHOLDERS OR TODOs:** Every script must be 100% complete, compilable, and production-ready before moving to the next task. Do not leave `// TODO` comments, stubbed methods, or simplified dummy math.

3. **INTERCONNECTIVITY WITH EXISTING REPO:** Design public APIs, interfaces, struct events (`ZeroGCEventBus`), and hooks so these scripts directly integrate with existing scripts in `unity-c--code`.

4. **STEP-BY-STEP LOOP FOR EACH TASK:**

   * **Step A:** Read the next unchecked `[ ]` task in the Master Task Queue below.

   * **Step B:** Inspect any referenced existing scripts in the repository to ensure exact signature/interface compatibility.

   * **Step C:** Implement the complete `.cs` file in the root directory of `unity-c--code` adhering to all Global Architectural Standards.

   * **Step D:** Perform a static self-review and Roslyn/Unity compilation check (verify zero GC allocations in hot loops, modern C# syntax, Unity New Input System compatibility where applicable, null safety, and Inspector serialization).

   * **Step E:** Update this `AGENT_TASKS.md` file by changing `[ ]` to `[x]` for the completed task, then proceed to the next task.

## 2. Global Architectural & Coding Standards

* **Zero-GC in Hot Paths:** Avoid LINQ (`System.Linq`), boxing of value types, closures/lambdas allocating inside `Update()`/`FixedUpdate()`/`LateUpdate()`/`OnAudioFilterRead()`, or `foreach` over non-list collections. Pre-allocate arrays, ring buffers, and `List<T>` capacities in `Awake()`.

* **Modern C# & Unity 6 Standards:** Utilize `readonly struct` for data/event payloads, `in` / `ref readonly` parameters to avoid struct copying, `Span<T>` / pre-allocated buffers where applicable, and `com.unity.inputsystem` (New Input System) if input is sampled.

* **Save/Load & Event Bus Integration:** Implement `ISaveableSystem` (from `SaveLoadSerializer.cs`) for stateful managers and publish `IGameEvent` structs via `ZeroGCEventBus.cs`.

* **Inspector & Scene View Debugging:** Include `[Header]`, `[Tooltip]`, `[SerializeField]`, and `[Range]` attributes. Implement `OnDrawGizmosSelected()` / `OnDrawGizmos()` for visual telemetry (vectors, trajectories, graphs, force lines).

* **Clean Documentation:** Provide concise XML documentation (`/// <summary>`) on public classes, structs, interfaces, and methods.

## 3. Master Sequential Task Queue (Phase 3 – 42 Scripts)

### Part A: Logic Circuits, Cybernetics, Networking & Cryptography

* [x] **Task 01: `LogicGateAndCircuitSimulator.cs`**

  * **Scope:** Complete digital & analog signal circuit simulator (inspired by *Factorio Circuit Network*, *Barotrauma*, and *Minecraft Redstone*).

  * **Requirements:**

    * Support logic gates (`AND`, `OR`, `XOR`, `NAND`, `NOR`, `NOT`), arithmetic/decider combinators, delay lines, oscillators/timers, SR/D-latches (memory cells), and sensors.

    * Implement multi-channel wire networks (e.g., Red/Green/Signal buses) carrying integer/float values per signal ID without GC allocations per tick.

    * Interconnect with `ResourceNetworkGraph.cs` and `ProductionChainNode.cs` so circuit signals can switch power relays or toggle factory production.

* [x] **Task 02: `InGameProgrammableVM.cs`**

  * **Scope:** Sandboxed, time-sliced in-game **Lua Scripting Engine & Bytecode Virtual Machine** (with optional Python-like indentation alias support) for programming drones, automated turrets, factory sorters, and logic controllers directly in-game (inspired by *ComputerCraft / CC: Tweaked*).

  * **Requirements:**

    * **Self-Contained Lua Compiler & Bytecode VM:** Implement a complete Lexer $\rightarrow$ Recursive Descent Parser $\rightarrow$ Bytecode Compiler (`LuaInstruction` struct array) $\rightarrow$ Stack/Register Virtual Machine Executor in pure C# (zero external DLL dependencies) supporting core Lua syntax: `local` variables, `if`/`then`/`elseif`/`else`/`end`, `while`/`do`/`end`, numeric `for`, `function` definitions & calls, 1-indexed Lua tables (`{}`), arithmetic/relational/logical operators, `sleep(seconds)`, and `yield()`.

    * **Zero-GC Runtime Execution:** Compilation occurs only on script load/edit. Runtime execution (`StepVM(int maxInstructionsPerTick)`) must operate strictly on pre-allocated `LuaValue[]` stack/register arrays using a discriminated union `readonly struct LuaValue` (`Nil`, `Boolean`, `Number`, `StringId`, `TableHandle`, `NativeFuncId`) with zero heap allocations per tick.

    * **Instruction Quota & Sandboxing:** Enforce a configurable `instructionsPerTick` quota (e.g., 256 ops/tick) and table memory limits so player-written `while true do end` infinite loops never freeze the Unity main thread.

    * **Hardware Peripheral & External Engine API (`ILuaScriptBackend`):** Expose a clean C# hardware binding API (e.g., `RegisterPeripheral("drone", ...)` allowing Lua calls like `drone.move(x, y)`, `turret.aim(angle)`, `circuit.setSignal(pin, val)`, `inv.transfer(id, count)`) plus an `ILuaScriptBackend` interface so the built-in VM can optionally wrap MoonSharp/NLua if ever desired.

    * **Interconnectivity:** Directly reads/writes signals in `LogicGateAndCircuitSimulator.cs`, executes automated hacking/defense daemons inside `VirtualNetworkAndHackingSim.cs`, and drives `ProceduralIKSolver2D.cs`, `UniversalInventoryContainer.cs`, and `PIDControllerAndAutopilotSuite.cs`.

* [x] **Task 03: `VirtualNetworkAndHackingSim.cs`**

  * **Scope:** In-game computer network topology and cybersecurity intrusion simulator.

  * **Requirements:**

    * Model subnets, routers, switches, firewalls, open/filtered ports, encrypted data nodes, honeypots, and Intrusion Detection Systems (IDS) with trace timers.

    * Simulate packet routing (TTL, source/destination IP, payload type) and active cyber-attacks (port scanning, buffer overflow exploit, packet sniffing, ARP spoofing, DDoS).

    * Integrate with `InGameProgrammableVM.cs` (allowing Lua scripts to run over network nodes) and `LogicGateAndCircuitSimulator.cs` (triggering physical locks/alarms upon breach).

* [x] **Task 04: `CryptographyAndSignalDecoderSim.cs`**

  * **Scope:** In-game cryptographic suite and radio signal interception/decoding module.

  * **Requirements:**

    * Implement classical and modern game-ready ciphers: Caesar shift, Vigenère, XOR stream cipher, substitution matrices, and simulated asymmetric key exchange.

    * Include a Frequency Analysis & Brute-Force Cracker state machine that progressively decodes intercepted ciphertexts based on compute power or signal-to-noise ratio (SNR).

    * Interconnect with `VirtualNetworkAndHackingSim.cs` (for cracking encrypted nodes/passwords) and `RadarAndEMSignatureSim.cs` (for intercepting radio transmissions).

### Part B: Engineering, Thermodynamics, Ballistics & Orbital Physics

* [x] **Task 05: `PIDControllerAndAutopilotSuite.cs`**

  * **Scope:** Industrial-grade Proportional-Integral-Derivative (PID) regulation controllers and multi-axis autopilot solver.

  * **Requirements:**

    * Provide reusable, allocation-free `FloatPIDController` and `Vector3PIDController` structs featuring integral windup clamping, derivative-on-measurement (to prevent derivative kick on setpoint change), and low-pass derivative filtering.

    * Include a modular `AutopilotController` MonoBehaviour supporting altitude hold, heading hold, velocity matching, and target stabilization.

* [x] **Task 06: `ThermalAndCoolantLoopSim.cs`**

  * **Scope:** Thermodynamics, heat conduction, radiators, and closed-loop coolant simulation (inspired by *Oxygen Not Included* and *Barotrauma* nuclear reactors).

  * **Requirements:**

    * Simulate thermal nodes with specific heat capacity ($Q = m \cdot c \cdot \Delta T$), thermal conductivity between adjacent nodes, ambient radiation cooling, and phase change (coolant boiling into high-pressure steam).

    * Model coolant pumps, heat exchangers, radiators, and catastrophic reactor/engine meltdown events when temperature exceeds structural thresholds.

    * Connect directly with `PIDControllerAndAutopilotSuite.cs` (automated control rods / cooling valves) and `DamageControlAndCompartmentSim.cs`.

* [x] **Task 07: `AerodynamicsAndLiftSurfaceSolver.cs`**

  * **Scope:** Component-based aerodynamic airfoil solver calculating real-time Lift, Drag, Angle of Attack (AoA), and Stall across individual wings, control surfaces, and fuselage segments.

  * **Requirements:**

    * Compute local relative airflow velocity (accounting for linear velocity + angular velocity cross-product at the airfoil's world position) and Angle of Attack ($\alpha$).

    * Evaluate Lift ($L = \frac{1}{2} \rho v^2 S C_L(\alpha)$) and Induced/Parasitic Drag ($D = \frac{1}{2} \rho v^2 S C_D(\alpha)$) with realistic post-stall lift drop-off and control surface deflection (ailerons, elevators, rudder, flaps).

    * Support structural damage: losing or damaging a wing panel immediately causes asymmetric lift and realistic aerodynamic spin/roll.

* [x] **Task 08: `RopeAndPulleyWinchPhysics.cs`**

  * **Scope:** Mechanical force-transmitting cables, pulleys, cranes, and motorized winches extending `VerletRope.cs`.

  * **Requirements:**

    * Model motorized winches (spooling cable in/out under tension limits, drum torque, braking), intermediate pulley sheaves redirecting tension vectors, and mechanical advantage (block and tackle).

    * Apply bidirectional physics forces (`Rigidbody2D` / `Rigidbody`) between the winch anchor and suspended payload, supporting cable snapping when tension exceeds `breakingStrengthNewtons`.

* [x] **Task 09: `BallisticsAndPenetrationSolver.cs`**

  * **Scope:** Physical armor penetration and internal terminal ballistics solver (inspired by *War Thunder* and *RimWorld Combat Extended*).

  * **Requirements:**

    * Calculate impact obliquity angle and Line-of-Sight effective armor thickness ($T_{\text{eff}} = T / \cos(\theta)$).

    * Model ricochet probability curves at extreme angles, normalization, overmatch, and distinct ammunition types: `AP` (kinetic DeMarre/Lanz-Odermatt penetration), `HE` (blast pressure & fragmentation), and `HEAT` (shaped-charge jet penetration independent of impact velocity).

    * Generate a secondary internal **Spalling Cone** (shrapnel rays inside the hull after penetration) damaging internal crew and modules in `DamageControlAndCompartmentSim.cs`.

* [x] **Task 10: `DamageControlAndCompartmentSim.cs`**

  * **Scope:** Modular vehicle/ship/submarine hull compartment and damage control simulator (inspired by *FTL* and *Barotrauma*).

  * **Requirements:**

    * Represent interconnected interior compartments (Reactor, Engines, Weapons, Life Support, Bridge) with bulkhead doors, hull integrity, flooding/decompression rates, fire hazards, and electrical short-circuits.

    * Interconnect with `BallisticsAndPenetrationSolver.cs` (hull breaches and module hits), `ResourceNetworkGraph.cs` (power grid blackouts), and `LogisticsJobSystem.cs` / AI agents (auto-dispatching repair and firefighting crews).

* [x] **Task 11: `RadarAndEMSignatureSim.cs`**

  * **Scope:** Electromagnetic spectrum, Radar Cross-Section (RCS), Infrared (IR) thermal signature, active/passive sonar, and Electronic Warfare (Jamming) simulator.

  * **Requirements:**

    * Compute detection signal strength using the Radar Range Equation ($P_r \propto \frac{P_t G^2 \lambda^2 \sigma}{(4\pi)^3 R^4}$), accounting for target aspect angle RCS ($\sigma$), terrain/horizon line-of-sight masking, clutter, and background thermal noise.

    * Support Passive Receiver mode (detecting emitters without revealing own position), Active PingSweep mode, and Noise/Deception Jamming (lowering SNR or injecting ghost targets).

    * Hook into `FogOfWarManager.cs` and `HomingProjectile.cs`.

* [x] **Task 12: `OrbitalMechanicsAndGravitySolver.cs`**

  * **Scope:** Real-time $N$-body and patched-conics Keplerian orbital mechanics solver for space simulations.

  * **Requirements:**

    * Support symplectic Velocity Verlet / RK4 numerical integration for $N$-body gravitational interactions alongside analytic Keplerian orbital elements (Semi-major axis $a$, Eccentricity $e$, Inclination $i$, Apoapsis, Periapsis, True Anomaly $\nu$).

    * Compute real-time trajectory prediction arrays (future flight path nodes, Sphere of Influence transitions, Lagrange points, and $\Delta v$ maneuver node burn previews) with zero GC allocations.

### Part C: Financial Markets, Banking & Contraband

* [x] **Task 13: `OrderBookAndMatchingEngine.cs`**

  * **Scope:** High-frequency financial exchange Limit Order Book (LOB) and price-time priority matching engine extending `DynamicMarketEconomy.cs`.

  * **Requirements:**

    * Maintain sorted Bid (buy) and Ask (sell) order books per asset supporting `LimitOrder`, `MarketOrder`, `StopLossOrder`, and `CancelOrder` using pre-allocated struct pools.

    * Compute live Bid-Ask Spread, Mid-Price, Market Depth (Level 2 book), VWAP, and slippage on large orders, enabling emergent market phenomena (short squeezes, flash crashes, liquidity crises).

    * Sync matched trade execution prices directly back into `DynamicMarketEconomy.cs`.

* [x] **Task 14: `BankingCreditAndInflationEngine.cs`**

  * **Scope:** Central and commercial banking simulator handling loans, interest rates, credit ratings, bonds, mortgages, and defaults.

  * **Requirements:**

    * Model fractional-reserve lending, central bank base interest rates, compound interest accrual, amortization schedules, credit score calculation ($300$–$850$), and sovereign/corporate bond issuance.

    * Handle loan defaults, collateral seizure (interfacing with `UniversalInventoryContainer.cs` and `GridPlacementSystem.cs`), money supply expansion, and resulting macroeconomic inflation fed into `DynamicMarketEconomy.cs`.

* [x] **Task 15: `ContrabandAndCustomsInspectionSim.cs`**

  * **Scope:** Smuggling, hidden cargo compartments, forged manifests, customs scanners, and bribery simulation.

  * **Requirements:**

    * Extend `UniversalInventoryContainer.cs` and `LogisticsJobSystem.cs` with concealed compartment capacity, shielding ratings, and declared vs. actual cargo manifests.

    * Simulate border/port inspection checkpoints using detection probability curves based on scanner tech (`RadarAndEMSignatureSim.cs`), inspector suspicion, faction reputation, bribery attempts, cargo confiscation, and black-market price multipliers.

### Part D: Forensics, Society, Genetics & Epidemics

* [x] **Task 16: `CrimeSceneAndForensicsTracker.cs`**

  * **Scope:** Procedural forensic evidence deposition and investigation system (inspired by *Shadows of Doubt*).

  * **Requirements:**

    * Record spatial-temporal physical evidence in a fast spatial structure: fingerprints on interacted objects, shoeprints by surface type, spent shell casings & bullet trajectories from `BallisticsAndPenetrationSolver.cs`, blood splatter, DNA traces, and CCTV sightings.

    * Simulate evidence degradation/contamination over time and provide zero-GC query methods for detectives/AI to match forensic traits against suspect databases.

* [x] **Task 17: `SocialNetworkAndConspiracyGraph.cs`**

  * **Scope:** Directed weighted social graph modeling relationships, debts, blackmail, family trees, and covert cell hierarchies across hundreds of NPCs.

  * **Requirements:**

    * Represent edges between NPCs (`Trust`, `Fear`, `Debt`, `Romantic`, `Kinship`, `CommandHierarchy`, `Blackmail`) in cache-friendly adjacency lists.

    * Support procedural generation of secret organizations/cults/resistance cells (Compartmentalized Cell Structure) and interrogation/investigation propagation (compromising one node reveals connected handlers or subordinates).

* [x] **Task 18: `GeneticsAndTraitCrossbreeder.cs`**

  * **Scope:** Mendelian and polygenic inheritance, chromosomes, dominant/recessive alleles, crossbreeding, and mutation engine.

  * **Requirements:**

    * Encode entity genomes as compact bit-packed arrays or struct chromosomes with Dominant, Recessive, Co-dominant, and Epistatic gene expression rules.

    * Implement Meiosis crossover (recombination with linkage disequilibrium), point mutations, inbreeding depression calculation (Wright's coefficient of inbreeding $F$), and phenotype stat evaluation hooking into `EcosystemEvolutionSim.cs`.

* [x] **Task 19: `EpidemicAndPathogenSpreadSim.cs`**

  * **Scope:** Epidemiological SEIR (Susceptible-Exposed-Infectious-Recovered/Deceased) and pathogen mutation simulator.

  * **Requirements:**

    * Model transmission vectors (airborne, waterborne, physical contact, trade cargo via `LogisticsJobSystem.cs`), incubation periods, asymptomatic carriers, lethality, and immunity decay.

    * Link with `GeneticsAndTraitCrossbreeder.cs` for live pathogen strain mutations and `DynamicFactionAndTreatyEngine.cs` for automated quarantine lockdowns and border closures.

* [x] **Task 20: `MarkovNameAndLoreGenerator.cs`**

  * **Scope:** Deterministic procedural name, corporation, city, and historical lore generator using $N$-gram Markov chains and grammar templates.

  * **Requirements:**

    * Train character-level and syllable-level Markov transition tables from configurable training seed lists (supporting distinct cultural/linguistic phonetics) with zero runtime heap allocations during generation (`Span<char>` / `StringBuilder` pooling).

    * Include a context-free grammar template expander for generating historical chronicles, company names for `DynamicMarketEconomy.cs`, and faction treaties.

* [x] **Task 21: `DynamicFactionAndTreatyEngine.cs`**

  * **Scope:** Geopolitical diplomacy, border tension, casus belli, sanctions, and automated peace treaty negotiation engine.

  * **Requirements:**

    * Maintain an $N \times N$ faction diplomacy matrix tracking bilateral relations, trust, military threat, trade dependency, and active treaties (Non-Aggression, Defensive Pact, Trade Embargo, Open Borders, Vassalage).

    * Implement utility-based war exhaustion and peace conference evaluation where AI factions weigh territorial claims, reparations, and military balance to propose or accept treaties.

### Part E: Procedural Audio, Destructible Terrain, Portals & Determinism

* [x] **Task 22: `ProceduralAudioSynthesizer.cs`**

  * **Scope:** Real-time C# procedural audio synthesizer via `OnAudioFilterRead` generating engine roar, radar pings, alarms, and sci-fi SFX without `.wav` assets.

  * **Requirements:**

    * Implement lock-free, zero-GC DSP building blocks: Sine/Saw/Square/Triangle oscillators, FM (Frequency Modulation) operators, White/Pink noise generators, Biquad Low-Pass/Band-Pass filters, and ADSR envelopes.

    * Include specialized presets controllable at runtime: multi-cylinder engine synthesis (harmonic pulse train modulated by RPM and load), RWR radar warning tones, reactor hum, and laser/ballistic transients.

* [x] **Task 23: `MarchingSquaresDestructibleTerrain.cs`**

  * **Scope:** Real-time destructible 2D/2.5D scalar-field terrain with cave carving, explosion craters, and chunked mesh/collider regeneration.

  * **Requirements:**

    * Partition scalar density grids (`float[,]`) into dirty-flagged spatial chunks; evaluate the Marching Squares isoline algorithm with linear interpolation for smooth slopes.

    * Rebuild chunk `Mesh` and simplified `PolygonCollider2D` paths without GC spikes when `BallisticsAndPenetrationSolver.cs` explosions or mining tools carve the terrain.

* [x] **Task 24: `PortalAndNonEuclideanView2D.cs`**

  * **Scope:** Seamless 2D/2.5D spatial portals and non-euclidean corridors handling camera rendering, entity teleportation, raycasts, and projectiles.

  * **Requirements:**

    * Compute relative portal-to-portal transformation matrices (position, rotation, scale) and manage clone transforms for seamless visual crossing.

    * Provide static/instance raycast and trajectory redirection APIs so `ProjectileControl.cs`, `BallisticsAndPenetrationSolver.cs`, and `PixelLight2D` rays bend accurately through linked portals.

* [x] **Task 25: `DeterministicPRNGAndNoiseLibrary.cs`**

  * **Scope:** Standalone, cross-platform deterministic pseudo-random number generator and coherent noise library independent of `UnityEngine.Random`.

  * **Requirements:**

    * Implement value-type `Xorshift128Plus` and `SplitMix64` PRNG structs (completely serializable state for deterministic replays and save files).

    * Implement fast 2D/3D `OpenSimplex2`, `Worley` (Cellular), Fractal Brownian Motion (fBm), and Domain Warping noise functions with zero allocations.

### Part F: Advanced Automotive Physics, Drivetrain & Motorsport AI

* [x] **Task 26: `InternalCombustionAndTurboSim.cs`**

  * **Scope:** Physical internal combustion engine (ICE) and forced-induction (Turbocharger / Supercharger) simulator.

  * **Requirements:**

    * Simulate crankshaft angular velocity ($\text{RPM}$), flywheel rotational inertia, torque curve evaluation (`AnimationCurve`), closed-throttle engine braking (pumping losses), idle air control, and hard/soft Rev Limiter (ignition/fuel cut).

    * Model exhaust gas enthalpy driving turbine spool-up (Turbo Lag), wastegate boost pressure regulation, intake manifold pressure, and Blow-Off Valve (BOV) pressure release events feeding directly into `ProceduralAudioSynthesizer.cs`.

* [x] **Task 27: `DrivetrainAndDifferentialSolver.cs`**

  * **Scope:** Complete mechanical powertrain coupling engine to wheels: clutch, gearbox, and torque-biasing differentials.

  * **Requirements:**

    * Simulate friction clutch engagement (pedal bite point, slip heat, torque capacity), manual/sequential/automatic gearboxes (with genuine negative-ratio reverse gear, rev-matching downshifts, and strictly optional auto-shift toggle), and drivetrain layouts (`FWD`, `RWD`, `AWD` with center torque split).

    * Implement selectable differential models: `Open`, `Locked` (Spool), `Torsen` (Torque-Sensing), and `ClutchPackLSD` (1.5-way and 2-way Limited Slip Differential for controlled drifting).

* [x] **Task 28: `PacejkaTireAndDriftPhysics.cs`**

  * **Scope:** High-fidelity tire grip and drift physics model (simplified Pacejka "Magic Formula" / Brush Tire Model) for 2D top-down and 3D vehicles.

  * **Requirements:**

    * Compute per-wheel longitudinal Slip Ratio ($\kappa$) and lateral Slip Angle ($\alpha$), normal load ($F_z$) with dynamic longitudinal and lateral **Weight Transfer** during braking, acceleration, and cornering.

    * Apply the friction ellipse (combined slip coupling longitudinal traction and lateral cornering force) for smooth, controllable transitions into oversteer/drifting.

    * Simulate tire core/surface temperature, compound wear, surface grip multipliers (tarmac, gravel, wet, ice), and emit skidmark/smoke data.

* [x] **Task 29: `ActiveElectronicsAndDriverAssists.cs`**

  * **Scope:** Real-time automotive electronic control unit (ECU) stability and traction assists wired to wheel sensors.

  * **Requirements:**

    * **ABS (Anti-Lock Braking System):** High-frequency brake pressure modulation when individual wheel slip ratio exceeds lockup threshold.

    * **TCS (Traction Control System):** Ignition/throttle cut or brake application when driven wheels spin beyond optimal slip ratio.

    * **ESC (Electronic Stability Control):** Compares steering wheel intended yaw rate against actual chassis yaw rate and asymmetrically brakes individual outer/inner wheels to correct understeer or oversteer.

    * Include **Launch Control** (holding optimal RPM at standstill until clutch release).

* [x] **Task 30: `SuspensionAndAeroDownforceSim.cs`**

  * **Scope:** Spring-damper suspension kinematics, anti-roll bars, aerodynamic downforce, and slipstream drafting.

  * **Requirements:**

    * Compute spring compression ($F_s = -k \cdot x$), asymmetric shock absorber damping (distinct High/Low-speed **Bump** and **Rebound** rates), and **Anti-Roll Bar (Sway Bar)** torque transfer between left and right wheels on an axle.

    * Calculate speed-squared front/rear aerodynamic **Downforce** ($F_{\text{down}} = \frac{1}{2} \rho v^2 A C_L$) feeding directly into `PacejkaTireAndDriftPhysics.cs` normal wheel loads, Drag, and **Slipstream / Drafting** drag reduction when trailing behind another vehicle.

* [x] **Task 31: `MultiAxleTrailerAndHitchPhysics.cs`**

  * **Scope:** Articulated fifth-wheel semi-trailers, drawbar trailers, multi-trailer road trains, and tow-winch articulation physics.

  * **Requirements:**

    * Model hitch constraints, kingpin tongue weight transfer onto the tractor rear axle, trailer brake proportioning, and dynamic cargo mass/Center-of-Mass shifts read from `UniversalInventoryContainer.cs`.

    * Accurately simulate **Jackknifing** instability when tractor drive/brake forces exceed trailer lateral tire grip, plus articulated reversing kinematics.

* [x] **Task 32: `RacingLineAndApexOptimizer.cs`**

  * **Scope:** Automated racing line solver computing the optimal geometric/minimum-curvature path and braking zones along `SplineRoadBuilder.cs` tracks.

  * **Requirements:**

    * Sample track centerlines and widths from `SplineRoadBuilder.cs` and iteratively relax path nodes to minimize curvature (Outside Entry $\rightarrow$ Inside **Apex** $\rightarrow$ Outside Exit).

    * Run a forward-backward velocity profile pass based on the vehicle's maximum lateral G-force, aerodynamic downforce, and braking deceleration to precompute exact **Braking Points**, turn-in points, and overtaking corridors for AI racers.

* [x] **Task 33: `VehicleTelemetryAndDynoLogger.cs`**

  * **Scope:** Chassis dynamometer (Dyno) analyzer and zero-GC motorsport telemetry logger.

  * **Requirements:**

    * Provide an automated **Dyno Sweep** routine calculating and plotting Engine Torque ($\text{Nm}$) and Power ($\text{kW}$ / $\text{HP} = \frac{\text{Torque} \times \text{RPM}}{7127}$) across the entire RPM range.

    * Record real-time telemetry into pre-allocated ring buffers: lateral/longitudinal **G-G Diagram**, sector & lap delta timers, brake rotor temperatures, suspension travel histograms, and tire slip percentages.

### Part G: Aviation Propulsion, Avionics, Carrier Ops, Dogfighting & ATC

* [x] **Task 34: `JetAndPropellerEngineSim.cs`**

  * **Scope:** Physical aircraft propulsion simulator covering Turbojet/Turbofan engines with Afterburner and Constant-Speed Variable-Pitch Propellers.

  * **Requirements:**

    * **Jet Engines:** Simulate N1/N2 turbine spool-up inertia lag, altitude/air-density thrust lapse, Mach ram-air recovery, multi-stage **Afterburner (Reheat)** thrust & fuel-flow spikes, **Compressor Stall / Flameout** at extreme Angle of Attack or intake damage, and 2D/3D **Thrust Vectoring** nozzles.

    * **Propeller Engines:** Simulate governor-controlled **Variable-Pitch (Constant-Speed) Propellers** adjusting blade pitch angle ($\beta$) to maintain optimal RPM, blade feathering on engine failure, and prop-wash airflow over wings.

* [x] **Task 35: `AircraftFuelAndCGShiftManager.cs`**

  * **Scope:** Multi-tank aircraft fuel network with real-time Center of Gravity (CG) shifting, cross-feed balancing, external drop tanks, and leak modeling.

  * **Requirements:**

    * Track fuel mass across distinct wing, fuselage, and external **Drop Tanks**, continuously recalculating the aircraft's composite **Center of Gravity (CG)** and rotational moment of inertia tensor as fuel burns or ordnance is jettisoned.

    * Support automated/manual **Cross-Feed Pumps** to prevent dangerous pitch/roll trim imbalances, emergency **Fuel Dumping**, and self-sealing vs. leaking tanks when punctured by `BallisticsAndPenetrationSolver.cs`.

* [x] **Task 36: `FlyByWireAndFlightEnvelopeProtection.cs`**

  * **Scope:** Digital Fly-By-Wire (FBW) flight control computer and aerodynamic envelope protection law wired to `PIDControllerAndAutopilotSuite.cs` and `AerodynamicsAndLiftSurfaceSolver.cs`.

  * **Requirements:**

    * Translate pilot stick inputs into commanded Pitch Rate ($q$), Roll Rate ($p$), and sideslip ($\beta = 0$ turn coordination) using cascaded PID loops and automatic pitch **Auto-Trim**.

    * Implement hard & soft **Flight Envelope Protections**: **Alpha Floor / Stall Prevention** (limiting AoA before critical stall), **G-Limiter** (preventing airframe over-stress structural failure), overspeed protection, and an "Override / Direct Law" toggle for extreme cobras/post-stall maneuvers.

* [x] **Task 37: `GFatigueAndPilotPhysiologySim.cs`**

  * **Scope:** Aerospace medicine physiology simulator modeling positive/negative G-forces, cerebral blood pressure, Greyout, Redout, and G-LOC (G-induced Loss of Consciousness).

  * **Requirements:**

    * Integrate sustained vertical acceleration ($+G_z$ and $-G_z$) and G-onset rate over time against pilot stamina, cardiovascular recovery, Crew Skill level, and **Anti-G Suit** inflation pressure.

    * Compute progressive visual/control impairment states: peripheral tunnel vision, color desaturation (**Greyout**), complete blackout, **G-LOC** (temporary total loss of pilot control inputs with disorientation wake-up delay), and negative-G **Redout**.

* [x] **Task 38: `ILSAndCarrierLandingSystem.cs`**

  * **Scope:** Instrument Landing System (ILS) precision approach beams and Naval Aircraft Carrier recovery/launch equipment (Arrestor Cables, Tailhook, Optical Landing System, and Catapult).

  * **Requirements:**

    * Compute **Localizer** (lateral azimuth) and **Glide Slope** (vertical $3^\circ$ descent path) angular deviation needles plus "Meatball" IFLOLS optical glideslope indicators even on a pitching/moving carrier deck.

    * Simulate **Tailhook** engagement with deck **Arrestor Wires** (interfacing with `VerletRope.cs` / `RopeAndPulleyWinchPhysics.cs` hydraulic damper runout to decelerate the jet from $250\text{ km/h}$ to $0$ in $90\text{ m}$), bolter detection, and **Steam / EMALS Catapult** launch stroke physics.

* [x] **Task 39: `FireControlAndLeadComputingSight.cs`**

  * **Scope:** Fighter aircraft and AA weapon Fire Control Computer computing gun lead (EEGS/LCOS), bomb impact points (CCIP/CCRP), and missile seeker locks.

  * **Requirements:**

    * **Guns (LCOS / EEGS):** Iteratively solve projectile time-of-flight and gravity drop against target position, velocity, and acceleration vectors to output an exact 3D/2D **Lead-Computing Pipper** position.

    * **Bombs (CCIP / CCRP):** Continuously compute the exact ground impact point (**CCIP**) and automated release cue (**CCRP**) for unguided ordnance accounting for aircraft velocity vector and wind.

    * **Missile Cueing:** Manage Radar STT/TWS lock and IR Seeker uncaged gimbal tracking feeding directly into `HomingProjectile.cs` and `RadarAndEMSignatureSim.cs`.

* [x] **Task 40: `CountermeasuresAndEWDispenser.cs`**

  * **Scope:** Airborne defensive countermeasures dispenser (Flares, Chaff, Towed Decoys) and Radar Warning Receiver (RWR) suite.

  * **Requirements:**

    * **Flares & Chaff:** Spawn pooled decoy entities with realistic aerodynamic deceleration, gravity fall, thermal IR intensity decay curves (seducing IR seekers in `HomingProjectile.cs`), and radar reflectivity blooms (cluttering `RadarAndEMSignatureSim.cs`). Support programmable salvo sequences.

    * **RWR (Radar Warning Receiver):** Detect incoming radar search, track (STT), and active missile launch emissions by azimuth and threat type, triggering synthesized audio threat warnings via `ProceduralAudioSynthesizer.cs`.

* [x] **Task 41: `DogfightAIAndBFMController.cs`**

  * **Scope:** Tactical Basic Fighter Maneuvers (BFM) AI brain based on Boyd's Energy-Maneuverability ($E_s = h + \frac{v^2}{2g}$) theory.

  * **Requirements:**

    * Evaluate geometry against bandit: Aspect Angle, Angle-Off-Tail, Closure Rate ($V_c$), Specific Energy state, and corner velocity.

    * Execute authentic 1-Circle (nose-to-nose) and 2-Circle (nose-to-tail) rate fights, **High/Low Yo-Yo**, **Immelmann**, **Split-S**, **Rolling Scissors**, and defensive missile evasion (**Notching / Beaming** perpendicular to Doppler radar + Chaff/Flare dispensing + **F-Pole / Crank** maneuvers).

* [x] **Task 42: `AirTrafficControlAndRunwayGraph.cs`**

  * **Scope:** Airport and military airbase Air Traffic Control (ATC) manager handling taxiway graphs, holding patterns, runway slot sequencing, and ground turnaround logistics.

  * **Requirements:**

    * Represent airport layouts as directed graphs of **Parking Aprons / Hangars**, **Taxiways**, **Hold-Short Nodes**, **Runways**, and airborne **Holding Pattern Stacks**.

    * Sequence departing and arriving aircraft with wake-turbulence separation intervals, issue wave-off / go-around commands if a runway is obstructed, and dispatch ground crew trucks via `LogisticsJobSystem.cs` for post-landing refueling (`AircraftFuelAndCGShiftManager.cs`) and rearming.

---

### Part F: Modern Inspector Tooling & UI Toolkit Suite

* [x] **Task 43: `VisualInspectorStudio`**
  * **Scope:** Production-grade custom Inspector and attribute drawer suite for Unity 6 UI Toolkit (`VisualInspectorAttributes.cs` in `/Core/` and `VisualInspectorEditor.cs` in `/Assets/Editor/VisualInspectorStudio/`).
  * **Requirements:**
    * `[Button]`, `[ShowIf]`, `[HideIf]`, `[ReadOnlyInspector]`, `[ProgressBar]`, `[MinMaxSlider]`, `[BoxGroup]`, `[TabGroup]`, `[RequiredField]`, and `[TelemetryGraph]`.
    * Zero third-party dependencies, UI Toolkit `isFallback = true` for `MonoBehaviour` and `ScriptableObject`, Undo support, dynamic `TrackPropertyValue`, and vector graphs.
