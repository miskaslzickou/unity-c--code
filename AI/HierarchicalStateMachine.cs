using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameSystems.FSM
{
    // =========================================================================
    // 1. ZÁKLADNÍ ROZHRANÍ PRO STAVY A PŘECHODY HFSM
    // =========================================================================

    /// <summary>
    /// Rozhraní pro libovolný uzel hierarchického stavového automatu.
    /// </summary>
    public interface IState<TContext>
    {
        string Name { get; }
        IState<TContext> Parent { get; set; }
        IState<TContext> ActiveChild { get; }

        void OnEnter();
        void OnUpdate(float deltaTime);
        void OnFixedUpdate(float fixedDeltaTime);
        void OnExit();
    }

    /// <summary>
    /// Přechod mezi dvěma stavy řízený podmínkou (guard) nebo spouštěčem (trigger).
    /// </summary>
    public class StateTransition<TContext>
    {
        public IState<TContext> From { get; }
        public IState<TContext> To { get; }
        public Func<bool> Condition { get; }
        public int Priority { get; }
        public string TriggerName { get; }

        public bool IsTrigger => !string.IsNullOrEmpty(TriggerName);

        public StateTransition(IState<TContext> from, IState<TContext> to, Func<bool> condition, int priority = 0, string triggerName = null)
        {
            From = from;
            To = to ?? throw new ArgumentNullException(nameof(to));
            Condition = condition;
            Priority = priority;
            TriggerName = triggerName;
        }

        public bool CanTransition()
        {
            return Condition == null || Condition();
        }
    }

    // =========================================================================
    // 2. ZÁKLADNÍ IMPLEMENTACE STAVU (LEAF NEBO KOMPOZITNÍ PARENT)
    // =========================================================================

    /// <summary>
    /// Čistá C# třída stavu s podporou hierarchického vnoření bez MonoBehaviour režií.
    /// </summary>
    public class State<TContext> : IState<TContext>
    {
        public string Name { get; }
        public TContext Context { get; protected set; }
        public IState<TContext> Parent { get; set; }
        public IState<TContext> ActiveChild => subStateMachine?.CurrentState;

        protected readonly StateMachine<TContext> subStateMachine;

        public State(string name, TContext context, bool isHierarchical = false)
        {
            Name = name ?? GetType().Name;
            Context = context;

            if (isHierarchical)
            {
                subStateMachine = new StateMachine<TContext>(context, this);
            }
        }

        public StateMachine<TContext> SubStateMachine => subStateMachine;

        public virtual void OnEnter()
        {
            subStateMachine?.OnEnter();
        }

        public virtual void OnUpdate(float deltaTime)
        {
            subStateMachine?.OnUpdate(deltaTime);
        }

        public virtual void OnFixedUpdate(float fixedDeltaTime)
        {
            subStateMachine?.OnFixedUpdate(fixedDeltaTime);
        }

        public virtual void OnExit()
        {
            subStateMachine?.OnExit();
        }
    }

    // =========================================================================
    // 3. JÁDRO HIERARCHICKÉHO STAVOVÉHO AUTOMATU (HFSM)
    // =========================================================================

    /// <summary>
    /// Generický Hierarchický Konečný Automat (HFSM) pro taktickou AI, pracovníky, továrny a vozidla.
    /// Podporuje vnořené stavy, prioritní přechody, Any-State přechody, triggery a historii stavů.
    /// </summary>
    public class StateMachine<TContext> : IState<TContext>
    {
        public string Name { get; }
        public TContext Context { get; }
        public IState<TContext> Parent { get; set; }
        public IState<TContext> ActiveChild => currentState;

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno při změně aktivního stavu (předchozí, nový).</summary>
        public event Action<IState<TContext>, IState<TContext>> OnStateChanged;

        /// <summary>Vyvoláno při vstupu do stavu.</summary>
        public event Action<IState<TContext>> OnStateEntered;

        /// <summary>Vyvoláno při opuštění stavu.</summary>
        public event Action<IState<TContext>> OnStateExited;

        /// <summary>Vyvoláno při aktivaci libovolného triggeru.</summary>
        public event Action<string> OnTriggerFired;

        // Vnitřní registry stavů a přechodů
        private readonly List<IState<TContext>> states = new List<IState<TContext>>(16);
        private readonly Dictionary<IState<TContext>, List<StateTransition<TContext>>> transitionsByState =
            new Dictionary<IState<TContext>, List<StateTransition<TContext>>>(16);
        private readonly List<StateTransition<TContext>> anyStateTransitions = new List<StateTransition<TContext>>(8);

        // Historie stavů pro RevertToPreviousState
        private readonly Stack<IState<TContext>> stateHistory = new Stack<IState<TContext>>(16);
        private const int MaxHistoryCount = 16;

        private IState<TContext> defaultState;
        private IState<TContext> currentState;
        private IState<TContext> previousState;
        private bool isRunning;

        public IState<TContext> CurrentState => currentState;
        public IState<TContext> PreviousState => previousState;
        public bool IsRunning => isRunning;

        public StateMachine(TContext context, IState<TContext> parent = null, string name = "StateMachine")
        {
            Context = context;
            Parent = parent;
            Name = name;
        }

        // =========================================================================
        // BUDOVÁNÍ AUTOMATU (CONFIG & REGISTRATION)
        // =========================================================================

        /// <summary>
        /// Nastaví výchozí stav, do kterého automat vstoupí při spuštění.
        /// </summary>
        public void SetDefaultState(IState<TContext> state)
        {
            defaultState = state;
            if (!states.Contains(state))
            {
                AddState(state);
            }
        }

        /// <summary>
        /// Přidá stav do automatu.
        /// </summary>
        public void AddState(IState<TContext> state)
        {
            if (state == null || states.Contains(state)) return;

            state.Parent = this;
            states.Add(state);

            if (!transitionsByState.ContainsKey(state))
            {
                transitionsByState[state] = new List<StateTransition<TContext>>(4);
            }

            if (defaultState == null)
            {
                defaultState = state;
            }
        }

        /// <summary>
        /// Přidá přechod řízený podmínkou mezi dvěma konkrétními stavy s prioritou.
        /// </summary>
        public void AddTransition(IState<TContext> from, IState<TContext> to, Func<bool> condition, int priority = 0)
        {
            AddState(from);
            AddState(to);

            var transition = new StateTransition<TContext>(from, to, condition, priority);
            List<StateTransition<TContext>> list = transitionsByState[from];
            list.Add(transition);

            // Řazení podle priority (vyšší priorita se vyhodnocuje dříve)
            list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>
        /// Přidá Any-State přechod, který může nastat z libovolného aktivního stavu.
        /// </summary>
        public void AddAnyTransition(IState<TContext> to, Func<bool> condition, int priority = 100)
        {
            AddState(to);
            var transition = new StateTransition<TContext>(null, to, condition, priority);
            anyStateTransitions.Add(transition);
            anyStateTransitions.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>
        /// Přidá přechod aktivovaný konkrétním triggerem (událostí).
        /// </summary>
        public void AddTriggerTransition(IState<TContext> from, IState<TContext> to, string triggerName, Func<bool> guard = null, int priority = 50)
        {
            AddState(from);
            AddState(to);

            var transition = new StateTransition<TContext>(from, to, guard, priority, triggerName);
            List<StateTransition<TContext>> list = transitionsByState[from];
            list.Add(transition);
            list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        // =========================================================================
        // BĚHOVÝ CYKLUS (EXECUTION)
        // =========================================================================

        public void OnEnter()
        {
            isRunning = true;
            if (currentState == null && defaultState != null)
            {
                TransitionTo(defaultState, false);
            }
            else if (currentState != null)
            {
                currentState.OnEnter();
            }
        }

        public void OnUpdate(float deltaTime)
        {
            if (!isRunning) return;

            // 1. Kontrola Any-State přechodů
            int anyCount = anyStateTransitions.Count;
            for (int i = 0; i < anyCount; i++)
            {
                StateTransition<TContext> trans = anyStateTransitions[i];
                if (trans.To != currentState && !trans.IsTrigger && trans.CanTransition())
                {
                    TransitionTo(trans.To);
                    break;
                }
            }

            // 2. Kontrola přechodů aktivního stavu
            if (currentState != null && transitionsByState.TryGetValue(currentState, out List<StateTransition<TContext>> list))
            {
                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    StateTransition<TContext> trans = list[i];
                    if (!trans.IsTrigger && trans.CanTransition())
                    {
                        TransitionTo(trans.To);
                        break;
                    }
                }
            }

            // 3. Aktualizace aktivního stavu (který rekurzivně aktualizuje své potomky)
            if (currentState != null)
            {
                currentState.OnUpdate(deltaTime);
            }
        }

        public void OnFixedUpdate(float fixedDeltaTime)
        {
            if (!isRunning) return;

            if (currentState != null)
            {
                currentState.OnFixedUpdate(fixedDeltaTime);
            }
        }

        public void OnExit()
        {
            isRunning = false;
            if (currentState != null)
            {
                currentState.OnExit();
            }
        }

        // =========================================================================
        // PŘECHODY A MANIPULACE SE STAVY
        // =========================================================================

        /// <summary>
        /// Vyvolá trigger podle názvu, který může okamžitě vyvolat přechod.
        /// </summary>
        public bool FireTrigger(string triggerName)
        {
            if (string.IsNullOrEmpty(triggerName) || currentState == null) return false;

            OnTriggerFired?.Invoke(triggerName);

            if (transitionsByState.TryGetValue(currentState, out List<StateTransition<TContext>> list))
            {
                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    StateTransition<TContext> trans = list[i];
                    if (trans.IsTrigger && trans.TriggerName.Equals(triggerName, StringComparison.OrdinalIgnoreCase) && trans.CanTransition())
                    {
                        TransitionTo(trans.To);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Provede okamžitý přechod do cílového stavu.
        /// </summary>
        public void TransitionTo(IState<TContext> targetState, bool recordHistory = true)
        {
            if (targetState == null || targetState == currentState) return;

            IState<TContext> oldState = currentState;

            // 1. Opuštění stávajícího stavu (včetně rekurzivního opuštění dětí)
            if (oldState != null)
            {
                oldState.OnExit();
                OnStateExited?.Invoke(oldState);

                if (recordHistory)
                {
                    if (stateHistory.Count >= MaxHistoryCount)
                    {
                        // Ořezání nejstaršího záznamu při překročení kapacity
                        var array = stateHistory.ToArray();
                        stateHistory.Clear();
                        for (int i = array.Length - 2; i >= 0; i--)
                        {
                            stateHistory.Push(array[i]);
                        }
                    }
                    stateHistory.Push(oldState);
                }
            }

            previousState = oldState;
            currentState = targetState;

            // 2. Vstup do nového stavu
            currentState.OnEnter();
            OnStateEntered?.Invoke(currentState);
            OnStateChanged?.Invoke(oldState, currentState);
        }

        /// <summary>
        /// Vrátí se do bezprostředně předchozího stavu v historii.
        /// </summary>
        public bool RevertToPreviousState()
        {
            if (stateHistory.Count > 0)
            {
                IState<TContext> prev = stateHistory.Pop();
                TransitionTo(prev, false);
                return true;
            }
            return false;
        }

        // =========================================================================
        // LADÍCÍ CESTA A REPREZENTACE
        // =========================================================================

        /// <summary>
        /// Vrátí hierarchickou cestu aktuálního stavu (např. "EnemyAI/Combat/MeleeAttack").
        /// </summary>
        public string GetActiveStateHierarchyPath()
        {
            if (currentState == null) return $"{Name}/[None]";

            if (currentState is StateMachine<TContext> subFsm)
            {
                return $"{Name}/{subFsm.GetActiveStateHierarchyPath()}";
            }

            if (currentState.ActiveChild != null)
            {
                return $"{Name}/{currentState.Name}/{currentState.ActiveChild.Name}";
            }

            return $"{Name}/{currentState.Name}";
        }
    }
}
