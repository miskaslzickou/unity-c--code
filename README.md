# Universal A* Pathfinding & Level Generation Toolkit for Unity

A clean, modular, and universal C# toolkit for Unity that combines robust **A* Pathfinding** (supporting both **Top-Down 2D** and **Platformer / Side-Scroller 2D**) with a **Procedural & Premade Level Generator**.

---

## 📁 Folder Structure
Place this folder in your project's `Assets/` directory:
```text
UniversalToolkit/
├── AStarPathfinder.cs          // Core A* pathfinding algorithm (Top-Down & Platformer dimensions)
├── UniversalPathfindingAgent.cs // AI Agent component that follows A* paths with physics
├── UniversalLevelGenerator.cs  // Flexible generator (1D Side-Scroller + Top-Down Dungeon/Grid)
```

---

## 🚀 Components Included

### 1. `AStarPathfinder.cs`
- **Universal Dimension Switch**: Switch between `PathfindingDimension.TopDown2D` (4-way / 8-way grid movement) and `PathfindingDimension.Platformer2D` (jump arcs, gravity awareness, and platform hops).
- **Customizable Heuristics**: Calculates optimal paths using Manhattan or Octile distance metrics.
- **Tilemap Integration**: Automatically reads solid obstacles directly from any Unity `Tilemap`.

### 2. `UniversalPathfindingAgent.cs`
- Attach this to any enemy or NPC.
- Automatically queries `AStarPathfinder` at set intervals (`updateInterval`).
- Smoothly steers and moves the agent using `Rigidbody2D` (with automatic jump execution for platformer mode) or transforms.

### 3. `UniversalLevelGenerator.cs`
- **Horizontal 1D (Side-Scroller)**: Perlin noise height mapping combined with premade chunk/room sequencing (the logic from your previous game).
- **Top-Down 2D**: Room-based or Cellular Automata / Noise dungeon generation.
- **Premade Room Support**: Prefab tilemap stitching with automatic spawn marker detection (`PlayerSpawn`).

---

## 🎮 How to Use

1. **Setup Pathfinder**:
   - Create an empty GameObject in your scene named `PathfinderManager`.
   - Attach `AStarPathfinder.cs`.
   - Assign your obstacle/ground `Tilemap` to `Obstacle Tilemap` and choose your dimension (`TopDown2D` or `Platformer2D`).

2. **Add Pathfinding to Enemies**:
   - Attach `UniversalPathfindingAgent.cs` to your enemy prefab.
   - Assign the target (e.g., Player transform).

3. **Generate Levels**:
   - Attach `UniversalLevelGenerator.cs` to a manager object.
   - Configure segments, tile references, and generation mode.
