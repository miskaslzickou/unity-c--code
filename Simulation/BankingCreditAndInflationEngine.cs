using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;
using GameSystems.Construction;
using GameSystems.Inventory;

namespace GameSystems.Economy
{
    // =========================================================================
    // 1. BANKING & CREDIT ENUMS AND STRUCTS
    // =========================================================================

    public enum LoanStatus : byte
    {
        ActiveCurrent,
        GracePeriod,
        Delinquent,
        Defaulted,
        FullyRepaid
    }

    public enum CollateralType : byte
    {
        None,
        RealEstateBuildingGrid,
        InventoryStockCargo,
        CorporateBond
    }

    [Serializable]
    public class LoanContract
    {
        public int LoanId;
        public int BorrowerId;
        public string BorrowerName = "Industrial Mining Corp";
        public float PrincipalAmount = 100000f;
        public float OutstandingBalance = 100000f;
        public float AnnualInterestRate = 0.065f;
        public int TotalTermMonths = 24;
        public int RemainingMonths = 24;
        public float MonthlyPayment = 4454f;
        public int MissedPaymentCount = 0;
        public LoanStatus Status = LoanStatus.ActiveCurrent;

        [Header("Collateral")]
        public CollateralType Collateral = CollateralType.None;
        public Vector2Int CollateralGridCell;
        public UniversalInventoryContainer CollateralInventory;
        public float CollateralAppraisedValue = 120000f;
    }

    [Serializable]
    public class BondContract
    {
        public int BondId;
        public string IssuerName = "Sovereign Treasury";
        public float FaceValue = 1000f;
        public float CouponRate = 0.045f;
        public int MaturityMonths = 60;
        public int RemainingMonths = 60;
        public float MarketPrice = 980f;
        public bool IsDefaulted = false;
    }

    [Serializable]
    public class BankBranch
    {
        public int BranchId = 1;
        public string BranchName = "Central District Bank";
        public Vector3 WorldPosition;
        public float VaultCashReserves = 500000f;
        public float CustomerDeposits = 2000000f;
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct LoanIssuedEvent : IGameEvent
    {
        public readonly int LoanId;
        public readonly int BorrowerId;
        public readonly float Principal;
        public readonly float InterestRate;

        public LoanIssuedEvent(int loanId, int borrowerId, float principal, float rate)
        {
            LoanId = loanId;
            BorrowerId = borrowerId;
            Principal = principal;
            InterestRate = rate;
        }
    }

    public readonly struct LoanDefaultedEvent : IGameEvent
    {
        public readonly int LoanId;
        public readonly int BorrowerId;
        public readonly float DefaultedAmount;

        public LoanDefaultedEvent(int loanId, int borrowerId, float amount)
        {
            LoanId = loanId;
            BorrowerId = borrowerId;
            DefaultedAmount = amount;
        }
    }

    public readonly struct CollateralSeizedEvent : IGameEvent
    {
        public readonly int LoanId;
        public readonly CollateralType Type;
        public readonly float RecoveredValue;

        public CollateralSeizedEvent(int loanId, CollateralType type, float value)
        {
            LoanId = loanId;
            Type = type;
            RecoveredValue = value;
        }
    }

    public readonly struct InterestRateAdjustedEvent : IGameEvent
    {
        public readonly float PreviousRate;
        public readonly float NewRate;
        public readonly float CurrentInflation;

        public InterestRateAdjustedEvent(float prev, float next, float inflation)
        {
            PreviousRate = prev;
            NewRate = next;
            CurrentInflation = inflation;
        }
    }

    // =========================================================================
    // 3. MASTER BANKING, CREDIT & INFLATION ENGINE
    // =========================================================================

    /// <summary>
    /// Central and commercial banking simulation engine.
    /// Simulates fractional-reserve lending, compound interest accrual, credit scores (300-850),
    /// loan defaults, collateral seizure (via GridPlacementSystem and UniversalInventoryContainer),
    /// money supply expansion (M0, M1, M2), and feeds live inflation into DynamicMarketEconomy.
    /// Fully compatible with both 2D and 3D spatial branch/vault layouts.
    /// </summary>
    [DisallowMultipleComponent]
    public class BankingCreditAndInflationEngine : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static BankingCreditAndInflationEngine Instance { get; private set; }

        [Header("External Market Links")]
        [SerializeField] private DynamicMarketEconomy dynamicEconomy;
        [SerializeField] private GridPlacementSystem gridPlacementSystem;

        [Header("Central Bank Monetary Policy")]
        [Tooltip("Central Bank base policy rate (e.g. 0.035 = 3.5%)")]
        [SerializeField, Range(0f, 0.25f)] private float centralBankBaseRate = 0.035f;
        [Tooltip("Reserve Requirement Ratio (e.g. 0.10 = 10%)")]
        [SerializeField, Range(0.05f, 0.30f)] private float reserveRequirementRatio = 0.10f;
        [Tooltip("Target annual inflation rate")]
        [SerializeField, Range(0f, 0.10f)] private float targetInflationRate = 0.02f;

        [Header("Monetary Aggregates & Macroeconomics")]
        [SerializeField] private float baseMoneyM0 = 1000000f;  // Currency + Central reserves
        [SerializeField] private float broadMoneyM2 = 5000000f; // Deposits + Loans
        [SerializeField] private float velocityOfMoney = 1.4f;
        [SerializeField] private float computedMacroInflation = 1.0f;

        [Header("Commercial Loans & Bonds")]
        [SerializeField] private List<LoanContract> loans = new List<LoanContract>(64);
        [SerializeField] private List<BondContract> bonds = new List<BondContract>(32);
        [SerializeField] private List<BankBranch> branches = new List<BankBranch>(8);

        [Header("Spatial Mode")]
        [SerializeField] private bool is2DMode = false;

        private int nextLoanId = 1;
        private int nextBondId = 1;

        public string SaveKey => "BankingCreditAndInflationEngine";
        public float BaseInterestRate => centralBankBaseRate;
        public float CurrentInflationMultiplier => computedMacroInflation;
        public float BroadMoneySupply => broadMoneyM2;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(gameObject); return; }

            if (dynamicEconomy == null)
                dynamicEconomy = FindFirstObjectByType<DynamicMarketEconomy>();
            if (gridPlacementSystem == null)
                gridPlacementSystem = FindFirstObjectByType<GridPlacementSystem>();
        }

        private void OnEnable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, TickChannel.Economy);
        }

        private void OnDisable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, TickChannel.Economy);
        }

        // =========================================================================
        // PUBLIC API: CREDIT SCORE & LOAN MANAGEMENT
        // =========================================================================

        public int CalculateCreditScore(int borrowerId, float totalDebt, float liquidAssets, int historicalDefaults)
        {
            // Standard FICO range: 300 to 850
            float baseScore = 650f;

            // Debt to asset ratio
            float dti = totalDebt / Mathf.Max(1000f, liquidAssets);
            if (dti < 0.2f) baseScore += 80f;
            else if (dti < 0.5f) baseScore += 30f;
            else if (dti > 1.5f) baseScore -= 120f;

            // Historical defaults severely penalize score
            baseScore -= historicalDefaults * 100f;

            return Mathf.Clamp(Mathf.RoundToInt(baseScore), 300, 850);
        }

        public LoanContract IssueLoan(
            int borrowerId,
            string borrowerName,
            float principal,
            int termMonths,
            CollateralType collateralType = CollateralType.None,
            Vector2Int collateralCell = default,
            UniversalInventoryContainer collateralInventory = null)
        {
            int creditScore = CalculateCreditScore(borrowerId, principal, principal * 0.5f, 0);

            // Risk premium based on credit score
            float riskSpread = (850 - creditScore) * 0.00015f;
            float annualRate = centralBankBaseRate + 0.02f + riskSpread;

            // Amortization Annuity formula: P = L * [r(1+r)^n] / [(1+r)^n - 1]
            float monthlyRate = annualRate / 12f;
            float factor = Mathf.Pow(1f + monthlyRate, termMonths);
            float monthlyPayment = principal * (monthlyRate * factor) / (factor - 1f);

            var loan = new LoanContract
            {
                LoanId = nextLoanId++,
                BorrowerId = borrowerId,
                BorrowerName = borrowerName,
                PrincipalAmount = principal,
                OutstandingBalance = principal,
                AnnualInterestRate = annualRate,
                TotalTermMonths = termMonths,
                RemainingMonths = termMonths,
                MonthlyPayment = monthlyPayment,
                Status = LoanStatus.ActiveCurrent,
                Collateral = collateralType,
                CollateralGridCell = collateralCell,
                CollateralInventory = collateralInventory,
                CollateralAppraisedValue = principal * 1.25f
            };

            loans.Add(loan);

            // Fractional-reserve lending expands broad money supply (M2)
            broadMoneyM2 += principal;

            EventBus<LoanIssuedEvent>.Raise(new LoanIssuedEvent(loan.LoanId, borrowerId, principal, annualRate));
            return loan;
        }

        public BondContract IssueBond(string issuer, float faceValue, float couponRate, int termMonths)
        {
            var bond = new BondContract
            {
                BondId = nextBondId++,
                IssuerName = issuer,
                FaceValue = faceValue,
                CouponRate = couponRate,
                MaturityMonths = termMonths,
                RemainingMonths = termMonths,
                MarketPrice = faceValue
            };
            bonds.Add(bond);
            return bond;
        }

        public bool MakeLoanPayment(int loanId, float paymentAmount)
        {
            for (int i = 0; i < loans.Count; i++)
            {
                var l = loans[i];
                if (l.LoanId == loanId && l.Status != LoanStatus.FullyRepaid && l.Status != LoanStatus.Defaulted)
                {
                    float monthlyRate = l.AnnualInterestRate / 12f;
                    float interestDue = l.OutstandingBalance * monthlyRate;
                    float principalPaid = Mathf.Max(0f, paymentAmount - interestDue);

                    l.OutstandingBalance = Mathf.Max(0f, l.OutstandingBalance - principalPaid);
                    l.MissedPaymentCount = 0;
                    l.Status = (l.OutstandingBalance <= 1f) ? LoanStatus.FullyRepaid : LoanStatus.ActiveCurrent;

                    if (l.Status == LoanStatus.FullyRepaid)
                    {
                        broadMoneyM2 = Mathf.Max(baseMoneyM0, broadMoneyM2 - l.PrincipalAmount);
                    }
                    return true;
                }
            }
            return false;
        }

        // =========================================================================
        // SIMULATION TICK LOOP (AMORTIZATION, DEFAULTS & INFLATION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            // 1. Process Monthly Loan Amortization & Check Defaults
            for (int i = 0; i < loans.Count; i++)
            {
                var loan = loans[i];
                if (loan.Status == LoanStatus.FullyRepaid || loan.Status == LoanStatus.Defaulted)
                    continue;

                loan.RemainingMonths--;

                // If balance remains but borrower failed to pay
                if (loan.OutstandingBalance > 1f && loan.Status != LoanStatus.ActiveCurrent)
                {
                    loan.MissedPaymentCount++;

                    if (loan.MissedPaymentCount >= 3)
                    {
                        // Loan Default triggered!
                        TriggerLoanDefault(loan);
                    }
                    else if (loan.MissedPaymentCount == 1)
                    {
                        loan.Status = LoanStatus.GracePeriod;
                    }
                    else
                    {
                        loan.Status = LoanStatus.Delinquent;
                    }
                }
            }

            // 2. Monetary Policy: Taylor Rule Interest Rate Adjustment
            // i = r* + pi + 0.5(pi - pi*)
            float inflationGap = (computedMacroInflation - 1.0f) - targetInflationRate;
            float newPolicyRate = Mathf.Clamp(targetInflationRate + 0.02f + (0.5f * inflationGap), 0.005f, 0.20f);

            if (Mathf.Abs(newPolicyRate - centralBankBaseRate) > 0.0025f)
            {
                float prev = centralBankBaseRate;
                centralBankBaseRate = newPolicyRate;
                EventBus<InterestRateAdjustedEvent>.Raise(new InterestRateAdjustedEvent(prev, newPolicyRate, computedMacroInflation));
            }

            // 3. Macroeconomic Inflation (Quantity Theory of Money: M * V = P * Y)
            // Money supply expansion leads to proportionate inflation
            float baselineMoney = (baseMoneyM0 * velocityOfMoney) / reserveRequirementRatio;
            float expansionRatio = broadMoneyM2 / Mathf.Max(1f, baselineMoney);
            computedMacroInflation = Mathf.Clamp(1.0f + (expansionRatio - 1.0f) * 0.5f, 0.5f, 5.0f);

            // Feed updated inflation into DynamicMarketEconomy
            if (dynamicEconomy != null)
            {
                dynamicEconomy.GlobalInflation = computedMacroInflation;
            }
        }

        private void TriggerLoanDefault(LoanContract loan)
        {
            loan.Status = LoanStatus.Defaulted;
            EventBus<LoanDefaultedEvent>.Raise(new LoanDefaultedEvent(loan.LoanId, loan.BorrowerId, loan.OutstandingBalance));

            // Execute Collateral Seizure
            ExecuteCollateralSeizure(loan);
        }

        private void ExecuteCollateralSeizure(LoanContract loan)
        {
            float recovered = 0f;

            switch (loan.Collateral)
            {
                case CollateralType.RealEstateBuildingGrid:
                    if (gridPlacementSystem != null)
                    {
                        // Demolish or repossess building from borrower
                        bool demolished = gridPlacementSystem.DemolishBuildingAt(loan.CollateralGridCell);
                        if (demolished)
                        {
                            recovered = loan.CollateralAppraisedValue * 0.75f; // Auction value
                        }
                    }
                    break;

                case CollateralType.InventoryStockCargo:
                    if (loan.CollateralInventory != null)
                    {
                        // Seize inventory cargo value
                        recovered = loan.CollateralInventory.GetTotalValue();
                        loan.CollateralInventory.Clear();
                    }
                    break;

                case CollateralType.CorporateBond:
                    recovered = loan.CollateralAppraisedValue * 0.85f;
                    break;
            }

            loan.OutstandingBalance = Mathf.Max(0f, loan.OutstandingBalance - recovered);
            EventBus<CollateralSeizedEvent>.Raise(new CollateralSeizedEvent(loan.LoanId, loan.Collateral, recovered));
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class BankingSaveData
        {
            public float BaseRate;
            public float M2;
            public float Inflation;
            public List<LoanContract> SavedLoans = new List<LoanContract>();
        }

        public object CaptureState()
        {
            return new BankingSaveData
            {
                BaseRate = centralBankBaseRate,
                M2 = broadMoneyM2,
                Inflation = computedMacroInflation,
                SavedLoans = new List<LoanContract>(loans)
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<BankingSaveData>(stateJson);
            if (data == null) return;

            centralBankBaseRate = data.BaseRate;
            broadMoneyM2 = data.M2;
            computedMacroInflation = data.Inflation;
            loans.Clear();
            if (data.SavedLoans != null)
                loans.AddRange(data.SavedLoans);
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            // Draw Bank Branches
            for (int i = 0; i < branches.Count; i++)
            {
                var b = branches[i];
                Gizmos.color = Color.green;
                if (is2DMode)
                    Gizmos.DrawWireCube(b.WorldPosition, new Vector3(2f, 2f, 0.1f));
                else
                    Gizmos.DrawWireCube(b.WorldPosition, Vector3.one * 2f);
            }

            // Draw Collateralized Grid Properties
            for (int i = 0; i < loans.Count; i++)
            {
                var l = loans[i];
                if (l.Collateral == CollateralType.RealEstateBuildingGrid)
                {
                    Gizmos.color = l.Status == LoanStatus.Defaulted ? Color.red : (l.Status == LoanStatus.Delinquent ? Color.yellow : Color.cyan);
                    Vector3 cellWorld = new Vector3(l.CollateralGridCell.x, is2DMode ? l.CollateralGridCell.y : 0f, is2DMode ? 0f : l.CollateralGridCell.y);
                    Gizmos.DrawWireCube(cellWorld, Vector3.one);
                }
            }
        }
    }
}
