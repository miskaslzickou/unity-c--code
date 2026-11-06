using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Construction;

namespace GameSystems.Commands
{
    // =========================================================================
    // 1. ZÁKLADNÍ ROZHRANÍ PRO PŘÍKAZY (COMMAND PATTERN)
    // =========================================================================

    /// <summary>
    /// Základní rozhraní pro jakýkoliv herní příkaz s plnou podporou Undo a Redo.
    /// </summary>
    public interface IGameCommand
    {
        string Description { get; }
        float EconomicCost { get; }

        bool CanExecute();
        void Execute();
        void Undo();
    }

    // =========================================================================
    // 2. KOMPOZITNÍ MAKRO PŘÍKAZ (PRO VÍCE-DLAŽDICOVÉ AKCE)
    // =========================================================================

    /// <summary>
    /// Sdružuje více dílčích příkazů do jediné atomické akce.
    /// Např. tažení 20 dílů silnice nebo zbourání celého bloku se vrátí naráz jediným stiskem Ctrl+Z.
    /// </summary>
    public class CompositeMacroCommand : IGameCommand
    {
        public string Description { get; }
        private readonly List<IGameCommand> subCommands;

        public float EconomicCost
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < subCommands.Count; i++)
                {
                    total += subCommands[i].EconomicCost;
                }
                return total;
            }
        }

        public IReadOnlyList<IGameCommand> SubCommands => subCommands;

        public CompositeMacroCommand(string description, int initialCapacity = 16)
        {
            Description = description;
            subCommands = new List<IGameCommand>(initialCapacity);
        }

        public void AddCommand(IGameCommand command)
        {
            if (command != null)
            {
                subCommands.Add(command);
            }
        }

        public bool CanExecute()
        {
            for (int i = 0; i < subCommands.Count; i++)
            {
                if (!subCommands[i].CanExecute()) return false;
            }
            return subCommands.Count > 0;
        }

        public void Execute()
        {
            for (int i = 0; i < subCommands.Count; i++)
            {
                subCommands[i].Execute();
            }
        }

        public void Undo()
        {
            // Vrácení pod-příkazů v přesně opačném pořadí (LIFO)
            for (int i = subCommands.Count - 1; i >= 0; i--)
            {
                subCommands[i].Undo();
            }
        }
    }

    /// <summary>
    /// Univerzální příkaz pro rychlé lambda / delegátové akce bez nutnosti vytvářet novou třídu.
    /// </summary>
    public class GenericDelegateCommand : IGameCommand
    {
        public string Description { get; }
        public float EconomicCost { get; }

        private readonly Action executeAction;
        private readonly Action undoAction;
        private readonly Func<bool> canExecuteFunc;

        public GenericDelegateCommand(string description, Action onExecute, Action onUndo, float cost = 0f, Func<bool> canExecute = null)
        {
            Description = description;
            executeAction = onExecute ?? throw new ArgumentNullException(nameof(onExecute));
            undoAction = onUndo ?? throw new ArgumentNullException(nameof(onUndo));
            EconomicCost = cost;
            canExecuteFunc = canExecute;
        }

        public bool CanExecute() => canExecuteFunc == null || canExecuteFunc();
        public void Execute() => executeAction();
        public void Undo() => undoAction();
    }

    // =========================================================================
    // 3. PŘÍKAZY PRO STAVEBNÍ SYSTÉM (GRID PLACEMENT SYSTEM HOOKS)
    // =========================================================================

    public class PlaceBuildingCommand : IGameCommand
    {
        public string Description => $"Build {definition.DisplayName}";
        public float EconomicCost => definition.ConstructionCost;

        private readonly PlaceableBuildingSO definition;
        private readonly Vector2Int originCell;
        private readonly BuildingRotation rotation;
        private PlacedBuilding spawnedBuilding;

        public PlaceBuildingCommand(PlaceableBuildingSO def, Vector2Int origin, BuildingRotation rot)
        {
            definition = def;
            originCell = origin;
            rotation = rot;
        }

        public bool CanExecute()
        {
            return GridPlacementSystem.Instance != null &&
                   GridPlacementSystem.Instance.CanPlaceBuilding(definition, originCell, rotation);
        }

        public void Execute()
        {
            if (GridPlacementSystem.Instance != null)
            {
                GridPlacementSystem.Instance.TryPlaceBuilding(definition, originCell, rotation, out spawnedBuilding);
            }
        }

        public void Undo()
        {
            if (GridPlacementSystem.Instance != null && spawnedBuilding != null)
            {
                GridPlacementSystem.Instance.DemolishBuildingAt(originCell);
                spawnedBuilding = null;
            }
        }
    }

    public class DemolishBuildingCommand : IGameCommand
    {
        public string Description => $"Demolish {cachedDef?.DisplayName ?? "Building"}";
        public float EconomicCost => -(cachedDef?.ConstructionCost ?? 0f) * (cachedDef?.DemolishRefundRatio ?? 0.5f);

        private readonly Vector2Int targetCell;
        private PlaceableBuildingSO cachedDef;
        private BuildingRotation cachedRot;

        public DemolishBuildingCommand(Vector2Int cell)
        {
            targetCell = cell;
            if (GridPlacementSystem.Instance != null)
            {
                PlacedBuilding b = GridPlacementSystem.Instance.GetBuildingAt(cell);
                if (b != null)
                {
                    cachedDef = b.Definition;
                    cachedRot = b.Rotation;
                }
            }
        }

        public bool CanExecute()
        {
            return GridPlacementSystem.Instance != null &&
                   GridPlacementSystem.Instance.GetBuildingAt(targetCell) != null;
        }

        public void Execute()
        {
            if (GridPlacementSystem.Instance != null)
            {
                GridPlacementSystem.Instance.DemolishBuildingAt(targetCell);
            }
        }

        public void Undo()
        {
            if (GridPlacementSystem.Instance != null && cachedDef != null)
            {
                GridPlacementSystem.Instance.TryPlaceBuilding(cachedDef, targetCell, cachedRot, out _);
            }
        }
    }

    // =========================================================================
    // 4. HLAVNÍ MANAŽER PŘÍKAZŮ A HISTORIE (COMMAND BUFFER UNDO SYSTEM)
    // =========================================================================

    /// <summary>
    /// Centrální fronta příkazů Command Pattern s omezeným zásobníkem Undo/Redo historie,
    /// podporou atomických makro příkazů, ekonomických refundací a klávesových zkratek.
    /// </summary>
    [DisallowMultipleComponent]
    public class CommandBufferUndoSystem : MonoBehaviour
    {
        public static CommandBufferUndoSystem Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS PRO UI A EKONOMIKU)
        // =========================================================================

        /// <summary>Vyvoláno po úspěšném vykonání nového příkazu.</summary>
        public static event Action<IGameCommand> OnCommandExecuted;

        /// <summary>Vyvoláno při vrácení příkazu zpět (Undo).</summary>
        public static event Action<IGameCommand> OnCommandUndone;

        /// <summary>Vyvoláno při znovuvykonání příkazu (Redo).</summary>
        public static event Action<IGameCommand> OnCommandRedone;

        /// <summary>Vyvoláno při změně stavu historie (canUndo, canRedo, undoCount, redoCount).</summary>
        public static event Action<bool, bool> OnHistoryStateChanged;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Nastavení historie")]
        [SerializeField, Range(16, 256), Tooltip("Maximální počet kroků v Undo historii")]
        private int maxUndoSteps = 64;

        [Header("Klávesové zkratky")]
        [SerializeField] private bool enableKeyboardShortcuts = true;
        [SerializeField] private KeyCode undoKey = KeyCode.Z;
        [SerializeField] private KeyCode redoKey = KeyCode.Y;

        // =========================================================================
        // VNITŘNÍ ZÁSOBNÍKY HISTORIE (BOUNDED LIFO BUFFER)
        // =========================================================================

        private readonly LinkedList<IGameCommand> undoStack = new LinkedList<IGameCommand>();
        private readonly Stack<IGameCommand> redoStack = new Stack<IGameCommand>(64);

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI (PUBLIC API)
        // =========================================================================

        public bool CanUndo => undoStack.Count > 0;
        public bool CanRedo => redoStack.Count > 0;
        public int UndoCount => undoStack.Count;
        public int RedoCount => redoStack.Count;

        public string NextUndoDescription => CanUndo ? undoStack.Last.Value.Description : string.Empty;
        public string NextRedoDescription => CanRedo ? redoStack.Peek().Description : string.Empty;

        // =========================================================================
        // UNITY ŽIVOTNÍ CYKLUS
        // =========================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            if (!enableKeyboardShortcuts) return;

            bool isCtrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (isCtrl)
            {
                // Ctrl + Shift + Z nebo Ctrl + Y pro Redo
                bool isShift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

                if (Input.GetKeyDown(undoKey))
                {
                    if (isShift) Redo();
                    else Undo();
                }
                else if (Input.GetKeyDown(redoKey))
                {
                    Redo();
                }
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // =========================================================================
        // JÁDRO VYKONÁVÁNÍ, UNDO A REDO (ZERO-GC BOUNDED LIFO)
        // =========================================================================

        /// <summary>
        /// Vykoná zadaný příkaz, přidá jej do historie Undo a vyčistí Redo zásobník.
        /// </summary>
        public bool ExecuteCommand(IGameCommand command)
        {
            if (command == null || !command.CanExecute()) return false;

            // 1. Samotné vykonání příkazu
            command.Execute();

            // 2. Vložení do Undo zásobníku s omezenou kapacitou
            undoStack.AddLast(command);
            if (undoStack.Count > maxUndoSteps)
            {
                undoStack.RemoveFirst(); // Oříznutí nejstaršího příkazu
            }

            // 3. Nový příkaz maže celou větev Redo
            redoStack.Clear();

            OnCommandExecuted?.Invoke(command);
            OnHistoryStateChanged?.Invoke(CanUndo, CanRedo);
            return true;
        }

        /// <summary>
        /// Vrátí poslední vykonaný příkaz (Ctrl + Z).
        /// </summary>
        public bool Undo()
        {
            if (!CanUndo) return false;

            IGameCommand command = undoStack.Last.Value;
            undoStack.RemoveLast();

            command.Undo();
            redoStack.Push(command);

            OnCommandUndone?.Invoke(command);
            OnHistoryStateChanged?.Invoke(CanUndo, CanRedo);
            return true;
        }

        /// <summary>
        /// Znovu vykoná dříve vrácený příkaz (Ctrl + Y nebo Ctrl + Shift + Z).
        /// </summary>
        public bool Redo()
        {
            if (!CanRedo) return false;

            IGameCommand command = redoStack.Pop();
            command.Execute();
            undoStack.AddLast(command);

            OnCommandRedone?.Invoke(command);
            OnHistoryStateChanged?.Invoke(CanUndo, CanRedo);
            return true;
        }

        /// <summary>
        /// Vyčistí celou historii příkazů.
        /// </summary>
        public void ClearHistory()
        {
            undoStack.Clear();
            redoStack.Clear();
            OnHistoryStateChanged?.Invoke(false, false);
        }
    }
}
