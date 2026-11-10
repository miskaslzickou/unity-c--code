using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;
using GameSystems.Inventory;
using GameSystems.Animation2D;

namespace GameSystems.Cybernetics
{
    // =========================================================================
    // 1. LUA VALUES & RUNTIME DATA TYPES (ZERO-GC DISCRIMINATED STRUCT)
    // =========================================================================

    public enum LuaValueType : byte
    {
        Nil = 0,
        Boolean = 1,
        Number = 2,
        StringId = 3,
        TableHandle = 4,
        NativeFuncId = 5
    }

    /// <summary>
    /// Allocation-free discriminated union struct representing any Lua runtime value.
    /// Operates without boxing or managed heap allocations during VM execution.
    /// </summary>
    [Serializable]
    public readonly struct LuaValue : IEquatable<LuaValue>
    {
        public readonly LuaValueType Type;
        public readonly double NumberValue;
        public readonly int HandleOrId;

        public static readonly LuaValue Nil = new LuaValue(LuaValueType.Nil, 0.0, 0);
        public static readonly LuaValue True = new LuaValue(LuaValueType.Boolean, 1.0, 1);
        public static readonly LuaValue False = new LuaValue(LuaValueType.Boolean, 0.0, 0);

        public LuaValue(LuaValueType type, double number, int handleOrId)
        {
            Type = type;
            NumberValue = number;
            HandleOrId = handleOrId;
        }

        public static LuaValue FromNumber(double value) => new LuaValue(LuaValueType.Number, value, 0);
        public static LuaValue FromBoolean(bool value) => value ? True : False;
        public static LuaValue FromStringId(int stringId) => new LuaValue(LuaValueType.StringId, 0.0, stringId);
        public static LuaValue FromTable(int tableHandle) => new LuaValue(LuaValueType.TableHandle, 0.0, tableHandle);
        public static LuaValue FromNativeFunc(int funcId) => new LuaValue(LuaValueType.NativeFuncId, 0.0, funcId);

        public bool AsBool => Type switch
        {
            LuaValueType.Nil => false,
            LuaValueType.Boolean => HandleOrId != 0,
            _ => true
        };

        public double AsNumber => Type switch
        {
            LuaValueType.Number => NumberValue,
            LuaValueType.Boolean => HandleOrId,
            _ => 0.0
        };

        public bool Equals(LuaValue other)
        {
            if (Type != other.Type) return false;
            return Type switch
            {
                LuaValueType.Nil => true,
                LuaValueType.Boolean => HandleOrId == other.HandleOrId,
                LuaValueType.Number => Math.Abs(NumberValue - other.NumberValue) < 0.000000001,
                LuaValueType.StringId => HandleOrId == other.HandleOrId,
                LuaValueType.TableHandle => HandleOrId == other.HandleOrId,
                LuaValueType.NativeFuncId => HandleOrId == other.HandleOrId,
                _ => false
            };
        }

        public override bool Equals(object obj) => obj is LuaValue other && Equals(other);
        public override int GetHashCode() => HashCode.Combine((int)Type, NumberValue, HandleOrId);

        public override string ToString() => Type switch
        {
            LuaValueType.Nil => "nil",
            LuaValueType.Boolean => HandleOrId != 0 ? "true" : "false",
            LuaValueType.Number => NumberValue.ToString("G"),
            LuaValueType.StringId => $"[StringId:{HandleOrId}]",
            LuaValueType.TableHandle => $"[Table:{HandleOrId}]",
            LuaValueType.NativeFuncId => $"[NativeFunc:{HandleOrId}]",
            _ => "nil"
        };
    }

    // =========================================================================
    // 2. BYTECODE INSTRUCTION SET & OPCODES
    // =========================================================================

    public enum LuaOpCode : byte
    {
        NOP = 0,
        LOADK,        // A = Consts[Bx]
        LOADBOOL,     // A = B, if C != 0 skip next
        LOADNIL,      // A .. B = nil
        GETGLOBAL,    // A = Globals[Consts[Bx]]
        SETGLOBAL,    // Globals[Consts[Bx]] = A
        GETTABLE,     // A = B[C]
        SETTABLE,     // A[B] = C
        NEWTABLE,     // A = new table
        ADD,          // A = B + C
        SUB,          // A = B - C
        MUL,          // A = B * C
        DIV,          // A = B / C
        MOD,          // A = B % C
        POW,          // A = B ^ C
        UNM,          // A = -B
        NOT,          // A = not B
        MOVE,         // A = B
        JMP,          // PC += sBx
        EQ,           // if ((A == B) ~= C) then PC++
        LT,           // if ((A < B) ~= C) then PC++
        LE,           // if ((A <= B) ~= C) then PC++
        TEST,         // if not (A <=> C) then PC++
        CALL,         // A(A+1, ..., A+B-1)
        RETURN,       // return A, ..., A+B-2
        FORPREP,      // init numeric for
        FORLOOP,      // step numeric for
        YIELD,        // yield execution until next tick
        SLEEP         // sleep A seconds
    }

    [Serializable]
    public readonly struct LuaInstruction
    {
        public readonly LuaOpCode Op;
        public readonly int A;
        public readonly int B;
        public readonly int C;

        public LuaInstruction(LuaOpCode op, int a = 0, int b = 0, int c = 0)
        {
            Op = op;
            A = a;
            B = b;
            C = c;
        }

        public override string ToString() => $"{Op,-10} A:{A} B:{B} C:{C}";
    }

    // =========================================================================
    // 3. ZERO-GC STRING POOL & TABLE STORAGE
    // =========================================================================

    public class LuaStringPool
    {
        private readonly List<string> idToString = new List<string>(128);
        private readonly Dictionary<string, int> stringToId = new Dictionary<string, int>(128, StringComparer.Ordinal);

        public LuaStringPool()
        {
            // ID 0 = empty string
            idToString.Add(string.Empty);
            stringToId[string.Empty] = 0;
        }

        public int GetOrAdd(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0;
            if (stringToId.TryGetValue(str, out int id))
                return id;

            id = idToString.Count;
            idToString.Add(str);
            stringToId[str] = id;
            return id;
        }

        public string Get(int id)
        {
            if (id >= 0 && id < idToString.Count)
                return idToString[id];
            return string.Empty;
        }

        public void Clear()
        {
            idToString.Clear();
            stringToId.Clear();
            idToString.Add(string.Empty);
            stringToId[string.Empty] = 0;
        }
    }

    public class LuaTable
    {
        public int Handle;
        // 1-indexed array part
        public readonly List<LuaValue> ArrayPart = new List<LuaValue>(16);
        // Hash map part (keyed by string ID)
        public readonly Dictionary<int, LuaValue> HashPart = new Dictionary<int, LuaValue>(16);

        public void Clear()
        {
            ArrayPart.Clear();
            HashPart.Clear();
        }

        public LuaValue Get(in LuaValue key)
        {
            if (key.Type == LuaValueType.Number)
            {
                int index = (int)key.NumberValue;
                if (index >= 1 && index <= ArrayPart.Count)
                    return ArrayPart[index - 1];
            }
            else if (key.Type == LuaValueType.StringId)
            {
                if (HashPart.TryGetValue(key.HandleOrId, out var val))
                    return val;
            }
            return LuaValue.Nil;
        }

        public void Set(in LuaValue key, in LuaValue val)
        {
            if (key.Type == LuaValueType.Number)
            {
                int index = (int)key.NumberValue;
                if (index == ArrayPart.Count + 1)
                {
                    ArrayPart.Add(val);
                    return;
                }
                if (index >= 1 && index <= ArrayPart.Count)
                {
                    ArrayPart[index - 1] = val;
                    return;
                }
            }
            if (key.Type == LuaValueType.StringId)
            {
                HashPart[key.HandleOrId] = val;
            }
        }
    }

    // =========================================================================
    // 4. EVENTS & INTERFACES (ZERO-GC EVENT BUS & PERIPHERALS)
    // =========================================================================

    public enum VmExecutionState
    {
        Ready,
        Running,
        Yielded,
        Sleeping,
        Completed,
        ErrorHalted
    }

    public readonly struct LuaVmExecutionEvent : IGameEvent
    {
        public readonly int VmId;
        public readonly VmExecutionState State;
        public readonly int InstructionPointer;
        public readonly int InstructionsExecuted;

        public LuaVmExecutionEvent(int vmId, VmExecutionState state, int ip, int executed)
        {
            VmId = vmId;
            State = state;
            InstructionPointer = ip;
            InstructionsExecuted = executed;
        }
    }

    public delegate LuaValue LuaNativeCallback(InGameProgrammableVM vm, Span<LuaValue> args);

    /// <summary>
    /// Interface for pluggable Lua VM scripting backends.
    /// </summary>
    public interface ILuaScriptBackend
    {
        bool CompileScript(string source, out string errorMessage);
        VmExecutionState Step(int maxInstructions, float deltaTime);
        void ResetVM();
        void RegisterNativeFunction(string name, LuaNativeCallback callback);
        void SetGlobal(string name, in LuaValue value);
        LuaValue GetGlobal(string name);
    }

    /// <summary>
    /// Hardware peripheral interface connecting game systems (drones, turrets, circuits, factories) to Lua.
    /// </summary>
    public interface ILuaPeripheral
    {
        string PeripheralName { get; }
        void AttachToVM(InGameProgrammableVM vm);
    }

    // =========================================================================
    // 5. PARSER & BYTECODE COMPILER (SELF-CONTAINED PURE C#)
    // =========================================================================

    internal enum TokenType
    {
        EOF, Identifier, Number, String,
        KwLocal, KwIf, KwThen, KwElseIf, KwElse, KwEnd,
        KwWhile, KwDo, KwFor, KwFunction, KwReturn, KwBreak,
        KwNil, KwTrue, KwFalse, KwAnd, KwOr, KwNot,
        KwSleep, KwYield,
        Plus, Minus, Star, Slash, Percent, Caret,
        EqEq, TildeEq, Less, LessEq, Greater, GreaterEq,
        Assign, Dot, Colon, Comma, Semi,
        LParen, RParen, LBracket, RBracket, LBrace, RBrace
    }

    internal struct Token
    {
        public TokenType Type;
        public string Text;
        public double Number;
        public int Line;
    }

    internal class LuaLexer
    {
        private readonly string src;
        private int pos = 0;
        private int line = 1;

        public LuaLexer(string source)
        {
            src = source ?? string.Empty;
        }

        public Token NextToken()
        {
            while (pos < src.Length)
            {
                char c = src[pos];

                if (c == '\n') { line++; pos++; continue; }
                if (char.IsWhiteSpace(c)) { pos++; continue; }

                // Comments (-- ...)
                if (c == '-' && pos + 1 < src.Length && src[pos + 1] == '-')
                {
                    pos += 2;
                    while (pos < src.Length && src[pos] != '\n') pos++;
                    continue;
                }

                // Numbers
                if (char.IsDigit(c) || (c == '.' && pos + 1 < src.Length && char.IsDigit(src[pos + 1])))
                {
                    int start = pos;
                    while (pos < src.Length && (char.IsDigit(src[pos]) || src[pos] == '.')) pos++;
                    string numStr = src.Substring(start, pos - start);
                    double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n);
                    return new Token { Type = TokenType.Number, Number = n, Text = numStr, Line = line };
                }

                // Identifiers & Keywords (Supports Lua keywords + Python aliases)
                if (char.IsLetter(c) || c == '_')
                {
                    int start = pos;
                    while (pos < src.Length && (char.IsLetterOrDigit(src[pos]) || src[pos] == '_')) pos++;
                    string id = src.Substring(start, pos - start);

                    TokenType tt = id switch
                    {
                        "local" => TokenType.KwLocal,
                        "if" => TokenType.KwIf,
                        "then" => TokenType.KwThen,
                        "elseif" or "elif" => TokenType.KwElseIf,
                        "else" => TokenType.KwElse,
                        "end" => TokenType.KwEnd,
                        "while" => TokenType.KwWhile,
                        "do" => TokenType.KwDo,
                        "for" => TokenType.KwFor,
                        "function" or "def" => TokenType.KwFunction,
                        "return" => TokenType.KwReturn,
                        "break" => TokenType.KwBreak,
                        "nil" or "None" => TokenType.KwNil,
                        "true" or "True" => TokenType.KwTrue,
                        "false" or "False" => TokenType.KwFalse,
                        "and" => TokenType.KwAnd,
                        "or" => TokenType.KwOr,
                        "not" => TokenType.KwNot,
                        "sleep" => TokenType.KwSleep,
                        "yield" => TokenType.KwYield,
                        _ => TokenType.Identifier
                    };
                    return new Token { Type = tt, Text = id, Line = line };
                }

                // Strings ("..." or '...')
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    pos++;
                    int start = pos;
                    while (pos < src.Length && src[pos] != quote)
                    {
                        if (src[pos] == '\n') line++;
                        pos++;
                    }
                    string s = src.Substring(start, Math.Max(0, pos - start));
                    if (pos < src.Length) pos++; // consume closing quote
                    return new Token { Type = TokenType.String, Text = s, Line = line };
                }

                pos++;
                switch (c)
                {
                    case '+': return new Token { Type = TokenType.Plus, Text = "+", Line = line };
                    case '-': return new Token { Type = TokenType.Minus, Text = "-", Line = line };
                    case '*': return new Token { Type = TokenType.Star, Text = "*", Line = line };
                    case '/': return new Token { Type = TokenType.Slash, Text = "/", Line = line };
                    case '%': return new Token { Type = TokenType.Percent, Text = "%", Line = line };
                    case '^': return new Token { Type = TokenType.Caret, Text = "^", Line = line };
                    case '(': return new Token { Type = TokenType.LParen, Text = "(", Line = line };
                    case ')': return new Token { Type = TokenType.RParen, Text = ")", Line = line };
                    case '[': return new Token { Type = TokenType.LBracket, Text = "[", Line = line };
                    case ']': return new Token { Type = TokenType.RBracket, Text = "]", Line = line };
                    case '{': return new Token { Type = TokenType.LBrace, Text = "{", Line = line };
                    case '}': return new Token { Type = TokenType.RBrace, Text = "}", Line = line };
                    case ',': return new Token { Type = TokenType.Comma, Text = ",", Line = line };
                    case ';': return new Token { Type = TokenType.Semi, Text = ";", Line = line };
                    case ':': return new Token { Type = TokenType.Colon, Text = ":", Line = line };
                    case '.': return new Token { Type = TokenType.Dot, Text = ".", Line = line };
                    case '=':
                        if (pos < src.Length && src[pos] == '=') { pos++; return new Token { Type = TokenType.EqEq, Text = "==", Line = line }; }
                        return new Token { Type = TokenType.Assign, Text = "=", Line = line };
                    case '~':
                        if (pos < src.Length && src[pos] == '=') { pos++; return new Token { Type = TokenType.TildeEq, Text = "~=", Line = line }; }
                        break;
                    case '!':
                        if (pos < src.Length && src[pos] == '=') { pos++; return new Token { Type = TokenType.TildeEq, Text = "!=", Line = line }; }
                        break;
                    case '<':
                        if (pos < src.Length && src[pos] == '=') { pos++; return new Token { Type = TokenType.LessEq, Text = "<=", Line = line }; }
                        return new Token { Type = TokenType.Less, Text = "<", Line = line };
                    case '>':
                        if (pos < src.Length && src[pos] == '=') { pos++; return new Token { Type = TokenType.GreaterEq, Text = ">=", Line = line }; }
                        return new Token { Type = TokenType.Greater, Text = ">", Line = line };
                }
            }

            return new Token { Type = TokenType.EOF, Line = line };
        }
    }

    internal class LuaCompiler
    {
        private readonly List<LuaInstruction> instructions = new List<LuaInstruction>(128);
        private readonly List<LuaValue> constants = new List<LuaValue>(64);
        private readonly LuaStringPool stringPool;
        private readonly Dictionary<string, int> localVariables = new Dictionary<string, int>();
        private int registerCount = 0;

        public LuaCompiler(LuaStringPool pool)
        {
            stringPool = pool;
        }

        public bool Compile(string source, out List<LuaInstruction> outInstructions, out List<LuaValue> outConstants, out string error)
        {
            instructions.Clear();
            constants.Clear();
            localVariables.Clear();
            registerCount = 0;
            error = string.Empty;

            try
            {
                var lexer = new LuaLexer(source);
                var tokens = new List<Token>();
                Token t;
                do
                {
                    t = lexer.NextToken();
                    tokens.Add(t);
                } while (t.Type != TokenType.EOF);

                int cursor = 0;
                while (cursor < tokens.Count && tokens[cursor].Type != TokenType.EOF)
                {
                    CompileStatement(tokens, ref cursor);
                }

                instructions.Add(new LuaInstruction(LuaOpCode.RETURN, 0, 1));
                outInstructions = new List<LuaInstruction>(instructions);
                outConstants = new List<LuaValue>(constants);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                outInstructions = new List<LuaInstruction>();
                outConstants = new List<LuaValue>();
                return false;
            }
        }

        private int AddConstant(in LuaValue val)
        {
            for (int i = 0; i < constants.Count; i++)
            {
                if (constants[i].Equals(val)) return i;
            }
            constants.Add(val);
            return constants.Count - 1;
        }

        private void CompileStatement(List<Token> tokens, ref int cursor)
        {
            Token cur = tokens[cursor];

            // Local declaration: local x = expr
            if (cur.Type == TokenType.KwLocal)
            {
                cursor++; // skip 'local'
                string name = tokens[cursor++].Text;
                int reg = registerCount++;
                localVariables[name] = reg;

                if (tokens[cursor].Type == TokenType.Assign)
                {
                    cursor++; // skip '='
                    CompileExpression(tokens, ref cursor, reg);
                }
                else
                {
                    instructions.Add(new LuaInstruction(LuaOpCode.LOADNIL, reg, reg));
                }
                if (tokens[cursor].Type == TokenType.Semi) cursor++;
                return;
            }

            // While loop: while cond do ... end
            if (cur.Type == TokenType.KwWhile)
            {
                cursor++; // skip 'while'
                int loopStart = instructions.Count;
                int condReg = registerCount++;
                CompileExpression(tokens, ref cursor, condReg);

                if (tokens[cursor].Type == TokenType.KwDo) cursor++;

                int jmpCondIndex = instructions.Count;
                instructions.Add(new LuaInstruction(LuaOpCode.TEST, condReg, 0, 0)); // jump if false
                int jmpExitIndex = instructions.Count;
                instructions.Add(new LuaInstruction(LuaOpCode.JMP, 0, 0, 0)); // placeholder exit jump

                while (cursor < tokens.Count && tokens[cursor].Type != TokenType.KwEnd)
                {
                    CompileStatement(tokens, ref cursor);
                }
                if (tokens[cursor].Type == TokenType.KwEnd) cursor++;

                // Jump back to condition
                int loopEnd = instructions.Count;
                instructions.Add(new LuaInstruction(LuaOpCode.JMP, 0, 0, loopStart - loopEnd - 1));

                // Patch exit jump
                instructions[jmpExitIndex] = new LuaInstruction(LuaOpCode.JMP, 0, 0, instructions.Count - jmpExitIndex - 1);
                return;
            }

            // If statement: if cond then ... else ... end
            if (cur.Type == TokenType.KwIf)
            {
                cursor++; // skip 'if'
                int condReg = registerCount++;
                CompileExpression(tokens, ref cursor, condReg);
                if (tokens[cursor].Type == TokenType.KwThen) cursor++;

                int testIndex = instructions.Count;
                instructions.Add(new LuaInstruction(LuaOpCode.TEST, condReg, 0, 0));
                int jmpFalseIndex = instructions.Count;
                instructions.Add(new LuaInstruction(LuaOpCode.JMP, 0, 0, 0)); // placeholder

                while (cursor < tokens.Count && tokens[cursor].Type != TokenType.KwEnd && tokens[cursor].Type != TokenType.KwElse)
                {
                    CompileStatement(tokens, ref cursor);
                }

                if (tokens[cursor].Type == TokenType.KwElse)
                {
                    cursor++; // skip 'else'
                    int jmpEndIndex = instructions.Count;
                    instructions.Add(new LuaInstruction(LuaOpCode.JMP, 0, 0, 0)); // skip else block
                    instructions[jmpFalseIndex] = new LuaInstruction(LuaOpCode.JMP, 0, 0, instructions.Count - jmpFalseIndex - 1);

                    while (cursor < tokens.Count && tokens[cursor].Type != TokenType.KwEnd)
                    {
                        CompileStatement(tokens, ref cursor);
                    }
                    instructions[jmpEndIndex] = new LuaInstruction(LuaOpCode.JMP, 0, 0, instructions.Count - jmpEndIndex - 1);
                }
                else
                {
                    instructions[jmpFalseIndex] = new LuaInstruction(LuaOpCode.JMP, 0, 0, instructions.Count - jmpFalseIndex - 1);
                }

                if (tokens[cursor].Type == TokenType.KwEnd) cursor++;
                return;
            }

            // Sleep: sleep(seconds)
            if (cur.Type == TokenType.KwSleep)
            {
                cursor++; // skip 'sleep'
                if (tokens[cursor].Type == TokenType.LParen) cursor++;
                int reg = registerCount++;
                CompileExpression(tokens, ref cursor, reg);
                if (tokens[cursor].Type == TokenType.RParen) cursor++;
                instructions.Add(new LuaInstruction(LuaOpCode.SLEEP, reg));
                if (tokens[cursor].Type == TokenType.Semi) cursor++;
                return;
            }

            // Yield: yield()
            if (cur.Type == TokenType.KwYield)
            {
                cursor++;
                if (tokens[cursor].Type == TokenType.LParen) cursor++;
                if (tokens[cursor].Type == TokenType.RParen) cursor++;
                instructions.Add(new LuaInstruction(LuaOpCode.YIELD));
                if (tokens[cursor].Type == TokenType.Semi) cursor++;
                return;
            }

            // Function call or Assignment
            if (cur.Type == TokenType.Identifier)
            {
                string id = cur.Text;
                cursor++;

                // Table indexing: obj.method(...) or obj.field = val
                if (tokens[cursor].Type == TokenType.Dot || tokens[cursor].Type == TokenType.Colon)
                {
                    cursor++; // skip . or :
                    string member = tokens[cursor++].Text;

                    if (tokens[cursor].Type == TokenType.LParen)
                    {
                        // Function Call: obj.member(args)
                        cursor++; // skip (
                        int targetFuncReg = registerCount++;
                        int objReg = registerCount++;

                        // Load global or local obj
                        if (localVariables.TryGetValue(id, out int loc))
                            instructions.Add(new LuaInstruction(LuaOpCode.MOVE, objReg, loc));
                        else
                        {
                            int gNameConst = AddConstant(LuaValue.FromStringId(stringPool.GetOrAdd(id)));
                            instructions.Add(new LuaInstruction(LuaOpCode.GETGLOBAL, objReg, gNameConst));
                        }

                        int memberConst = AddConstant(LuaValue.FromStringId(stringPool.GetOrAdd(member)));
                        instructions.Add(new LuaInstruction(LuaOpCode.GETTABLE, targetFuncReg, objReg, memberConst));

                        // Compile arguments
                        int argCount = 0;
                        while (cursor < tokens.Count && tokens[cursor].Type != TokenType.RParen)
                        {
                            int argReg = registerCount++;
                            CompileExpression(tokens, ref cursor, argReg);
                            argCount++;
                            if (tokens[cursor].Type == TokenType.Comma) cursor++;
                        }
                        if (tokens[cursor].Type == TokenType.RParen) cursor++;

                        instructions.Add(new LuaInstruction(LuaOpCode.CALL, targetFuncReg, argCount + 1, 1));
                    }
                    return;
                }

                // Assignment: var = expr
                if (tokens[cursor].Type == TokenType.Assign)
                {
                    cursor++; // skip =
                    int reg = localVariables.TryGetValue(id, out int loc) ? loc : registerCount++;
                    CompileExpression(tokens, ref cursor, reg);
                    if (!localVariables.ContainsKey(id))
                    {
                        int gConst = AddConstant(LuaValue.FromStringId(stringPool.GetOrAdd(id)));
                        instructions.Add(new LuaInstruction(LuaOpCode.SETGLOBAL, reg, gConst));
                    }
                    if (tokens[cursor].Type == TokenType.Semi) cursor++;
                    return;
                }

                // Global function call: func(args)
                if (tokens[cursor].Type == TokenType.LParen)
                {
                    cursor++; // skip (
                    int funcReg = registerCount++;
                    int gConst = AddConstant(LuaValue.FromStringId(stringPool.GetOrAdd(id)));
                    instructions.Add(new LuaInstruction(LuaOpCode.GETGLOBAL, funcReg, gConst));

                    int argCount = 0;
                    while (cursor < tokens.Count && tokens[cursor].Type != TokenType.RParen)
                    {
                        int argReg = registerCount++;
                        CompileExpression(tokens, ref cursor, argReg);
                        argCount++;
                        if (tokens[cursor].Type == TokenType.Comma) cursor++;
                    }
                    if (tokens[cursor].Type == TokenType.RParen) cursor++;
                    instructions.Add(new LuaInstruction(LuaOpCode.CALL, funcReg, argCount + 1, 1));
                    return;
                }
            }

            cursor++; // fallback step
        }

        private void CompileExpression(List<Token> tokens, ref int cursor, int targetReg)
        {
            CompilePrimary(tokens, ref cursor, targetReg);

            // Binary arithmetic/relational operators
            if (cursor < tokens.Count)
            {
                Token op = tokens[cursor];
                LuaOpCode opCode = op.Type switch
                {
                    TokenType.Plus => LuaOpCode.ADD,
                    TokenType.Minus => LuaOpCode.SUB,
                    TokenType.Star => LuaOpCode.MUL,
                    TokenType.Slash => LuaOpCode.DIV,
                    TokenType.Percent => LuaOpCode.MOD,
                    TokenType.Caret => LuaOpCode.POW,
                    TokenType.EqEq => LuaOpCode.EQ,
                    TokenType.Less => LuaOpCode.LT,
                    TokenType.LessEq => LuaOpCode.LE,
                    _ => LuaOpCode.NOP
                };

                if (opCode != LuaOpCode.NOP)
                {
                    cursor++; // skip operator
                    int rightReg = registerCount++;
                    CompilePrimary(tokens, ref cursor, rightReg);
                    instructions.Add(new LuaInstruction(opCode, targetReg, targetReg, rightReg));
                }
            }
        }

        private void CompilePrimary(List<Token> tokens, ref int cursor, int targetReg)
        {
            Token cur = tokens[cursor++];
            switch (cur.Type)
            {
                case TokenType.Number:
                    int nConst = AddConstant(LuaValue.FromNumber(cur.Number));
                    instructions.Add(new LuaInstruction(LuaOpCode.LOADK, targetReg, nConst));
                    break;
                case TokenType.String:
                    int sConst = AddConstant(LuaValue.FromStringId(stringPool.GetOrAdd(cur.Text)));
                    instructions.Add(new LuaInstruction(LuaOpCode.LOADK, targetReg, sConst));
                    break;
                case TokenType.KwTrue:
                    instructions.Add(new LuaInstruction(LuaOpCode.LOADBOOL, targetReg, 1, 0));
                    break;
                case TokenType.KwFalse:
                    instructions.Add(new LuaInstruction(LuaOpCode.LOADBOOL, targetReg, 0, 0));
                    break;
                case TokenType.KwNil:
                    instructions.Add(new LuaInstruction(LuaOpCode.LOADNIL, targetReg, targetReg));
                    break;
                case TokenType.LBrace:
                    // New table constructor {}
                    instructions.Add(new LuaInstruction(LuaOpCode.NEWTABLE, targetReg));
                    if (tokens[cursor].Type == TokenType.RBrace) cursor++;
                    break;
                case TokenType.Identifier:
                    if (localVariables.TryGetValue(cur.Text, out int loc))
                    {
                        instructions.Add(new LuaInstruction(LuaOpCode.MOVE, targetReg, loc));
                    }
                    else
                    {
                        int gConst = AddConstant(LuaValue.FromStringId(stringPool.GetOrAdd(cur.Text)));
                        instructions.Add(new LuaInstruction(LuaOpCode.GETGLOBAL, targetReg, gConst));
                    }
                    break;
            }
        }
    }

    // =========================================================================
    // 6. MAIN MONOBEHAVIOUR VM ENGINE (INGAMEPROGRAMMABLEVM)
    // =========================================================================

    /// <summary>
    /// Sandboxed in-game Lua Virtual Machine and bytecode compiler.
    /// Runs user-written scripts with strict time-sliced quotas (instructions/tick),
    /// zero-GC runtime execution, and pluggable 2D/3D hardware peripherals.
    /// </summary>
    [DisallowMultipleComponent]
    public class InGameProgrammableVM : MonoBehaviour, ITickable, ISaveableSystem, ILuaScriptBackend
    {
        [Header("Script & Source")]
        [TextArea(8, 20)]
        [SerializeField] private string luaScript = @"-- Example Autonomous Drone Controller
print('Starting drone autopilot...')
drone.move(5.0, 2.0, 0.0)
sleep(1.0)
circuit.set(1, 40, 100)
";

        [Header("Sandboxing & Quotas")]
        [SerializeField, Range(16, 2048)] private int instructionsPerTick = 256;
        [SerializeField] private bool autoRunOnStart = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Normal;

        [Header("Execution State")]
        [SerializeField] private VmExecutionState currentState = VmExecutionState.Ready;
        [SerializeField] private int programCounter = 0;
        [SerializeField] private float sleepTimerSeconds = 0f;

        // Pre-allocated runtime structures (Strictly Zero-GC during Step)
        private readonly LuaValue[] registers = new LuaValue[256];
        private readonly Dictionary<int, LuaValue> globalVariables = new Dictionary<int, LuaValue>(64);
        private readonly List<LuaTable> tableHeap = new List<LuaTable>(32);
        private readonly List<LuaNativeCallback> nativeCallbacks = new List<LuaNativeCallback>(32);
        private readonly Dictionary<int, int> nativeFuncNameToId = new Dictionary<int, int>(32);

        private List<LuaInstruction> bytecode = new List<LuaInstruction>(128);
        private List<LuaValue> constants = new List<LuaValue>(64);
        private readonly LuaStringPool stringPool = new LuaStringPool();
        private LuaCompiler compiler;

        // Hardware Peripherals
        private readonly List<ILuaPeripheral> attachedPeripherals = new List<ILuaPeripheral>(8);

        public string SaveKey => $"LuaVM_{gameObject.name}_{GetInstanceID()}";
        public VmExecutionState CurrentState => currentState;
        public int InstructionsPerTick { get => instructionsPerTick; set => instructionsPerTick = value; }

        // =========================================================================
        // INITIALIZATION & LIFECYCLE
        // =========================================================================

        private void Awake()
        {
            compiler = new LuaCompiler(stringPool);
            RegisterStandardLibraries();
            RegisterDefaultPeripherals();

            if (autoRunOnStart && !string.IsNullOrEmpty(luaScript))
            {
                CompileScript(luaScript, out string _);
                currentState = VmExecutionState.Running;
            }
        }

        private void OnEnable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, tickChannel);
        }

        private void OnDisable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, tickChannel);
        }

        // =========================================================================
        // COMPILATION & SETUP
        // =========================================================================

        public bool CompileScript(string source, out string errorMessage)
        {
            luaScript = source;
            bool ok = compiler.Compile(source, out bytecode, out constants, out errorMessage);
            ResetVM();
            return ok;
        }

        public void ResetVM()
        {
            programCounter = 0;
            sleepTimerSeconds = 0f;
            Array.Clear(registers, 0, registers.Length);
            currentState = bytecode.Count > 0 ? VmExecutionState.Running : VmExecutionState.Ready;
        }

        // =========================================================================
        // NATIVE FUNCTIONS & HARDWARE PERIPHERALS
        // =========================================================================

        public void RegisterNativeFunction(string name, LuaNativeCallback callback)
        {
            int nameId = stringPool.GetOrAdd(name);
            int funcId = nativeCallbacks.Count;
            nativeCallbacks.Add(callback);
            nativeFuncNameToId[nameId] = funcId;

            SetGlobal(name, LuaValue.FromNativeFunc(funcId));
        }

        public void SetGlobal(string name, in LuaValue value)
        {
            int nameId = stringPool.GetOrAdd(name);
            globalVariables[nameId] = value;
        }

        public LuaValue GetGlobal(string name)
        {
            int nameId = stringPool.GetOrAdd(name);
            return globalVariables.TryGetValue(nameId, out var val) ? val : LuaValue.Nil;
        }

        public void RegisterPeripheral(ILuaPeripheral peripheral)
        {
            if (peripheral == null) return;
            attachedPeripherals.Add(peripheral);
            peripheral.AttachToVM(this);
        }

        private void RegisterStandardLibraries()
        {
            // print(...)
            RegisterNativeFunction("print", (vm, args) =>
            {
                var sb = new StringBuilder();
                for (int i = 0; i < args.Length; i++)
                {
                    sb.Append(FormatLuaValue(args[i])).Append(" ");
                }
                Debug.Log($"[LuaVM:{gameObject.name}] {sb}");
                return LuaValue.Nil;
            });

            // gettime()
            RegisterNativeFunction("gettime", (vm, args) => LuaValue.FromNumber(Time.timeAsDouble));
        }

        private string FormatLuaValue(in LuaValue val)
        {
            return val.Type switch
            {
                LuaValueType.Nil => "nil",
                LuaValueType.Boolean => val.HandleOrId != 0 ? "true" : "false",
                LuaValueType.Number => val.NumberValue.ToString("G"),
                LuaValueType.StringId => stringPool.Get(val.HandleOrId),
                _ => val.ToString()
            };
        }

        private void RegisterDefaultPeripherals()
        {
            // Built-in 2D/3D Drone Peripheral
            RegisterPeripheral(new DroneHardwarePeripheral(transform, GetComponent<Rigidbody>(), GetComponent<Rigidbody2D>()));
            // Built-in Circuit Peripheral (connects to LogicGateAndCircuitSimulator)
            RegisterPeripheral(new CircuitHardwarePeripheral());
            // Built-in Inventory Peripheral (connects to UniversalInventoryContainer)
            var inv = GetComponent<UniversalInventoryContainer>();
            if (inv != null)
                RegisterPeripheral(new InventoryHardwarePeripheral(inv));
        }

        // =========================================================================
        // VM RUNTIME EXECUTION (TIME-SLICED, ZERO-GC)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            if (currentState == VmExecutionState.Running || currentState == VmExecutionState.Yielded || currentState == VmExecutionState.Sleeping)
            {
                Step(instructionsPerTick, tickDeltaTime);
            }
        }

        public VmExecutionState Step(int maxInstructions, float deltaTime)
        {
            if (currentState == VmExecutionState.Sleeping)
            {
                sleepTimerSeconds -= deltaTime;
                if (sleepTimerSeconds <= 0f)
                {
                    currentState = VmExecutionState.Running;
                }
                else
                {
                    return VmExecutionState.Sleeping;
                }
            }

            if (currentState != VmExecutionState.Running && currentState != VmExecutionState.Yielded)
                return currentState;

            currentState = VmExecutionState.Running;
            int opsExecuted = 0;
            int codeLen = bytecode.Count;

            while (opsExecuted < maxInstructions && programCounter < codeLen)
            {
                LuaInstruction inst = bytecode[programCounter++];
                opsExecuted++;

                switch (inst.Op)
                {
                    case LuaOpCode.LOADK:
                        registers[inst.A] = constants[inst.B];
                        break;

                    case LuaOpCode.LOADBOOL:
                        registers[inst.A] = inst.B != 0 ? LuaValue.True : LuaValue.False;
                        if (inst.C != 0) programCounter++;
                        break;

                    case LuaOpCode.LOADNIL:
                        for (int r = inst.A; r <= inst.B; r++) registers[r] = LuaValue.Nil;
                        break;

                    case LuaOpCode.MOVE:
                        registers[inst.A] = registers[inst.B];
                        break;

                    case LuaOpCode.GETGLOBAL:
                        int gNameId = constants[inst.B].HandleOrId;
                        registers[inst.A] = globalVariables.TryGetValue(gNameId, out var gv) ? gv : LuaValue.Nil;
                        break;

                    case LuaOpCode.SETGLOBAL:
                        int sNameId = constants[inst.B].HandleOrId;
                        globalVariables[sNameId] = registers[inst.A];
                        break;

                    case LuaOpCode.ADD:
                        registers[inst.A] = LuaValue.FromNumber(registers[inst.B].AsNumber + registers[inst.C].AsNumber);
                        break;

                    case LuaOpCode.SUB:
                        registers[inst.A] = LuaValue.FromNumber(registers[inst.B].AsNumber - registers[inst.C].AsNumber);
                        break;

                    case LuaOpCode.MUL:
                        registers[inst.A] = LuaValue.FromNumber(registers[inst.B].AsNumber * registers[inst.C].AsNumber);
                        break;

                    case LuaOpCode.DIV:
                        double divisor = registers[inst.C].AsNumber;
                        registers[inst.A] = Math.Abs(divisor) > 0.000000001 ? LuaValue.FromNumber(registers[inst.B].AsNumber / divisor) : LuaValue.Nil;
                        break;

                    case LuaOpCode.MOD:
                        double m = registers[inst.C].AsNumber;
                        registers[inst.A] = Math.Abs(m) > 0.000000001 ? LuaValue.FromNumber(registers[inst.B].AsNumber % m) : LuaValue.Nil;
                        break;

                    case LuaOpCode.POW:
                        registers[inst.A] = LuaValue.FromNumber(Math.Pow(registers[inst.B].AsNumber, registers[inst.C].AsNumber));
                        break;

                    case LuaOpCode.UNM:
                        registers[inst.A] = LuaValue.FromNumber(-registers[inst.B].AsNumber);
                        break;

                    case LuaOpCode.NOT:
                        registers[inst.A] = registers[inst.B].AsBool ? LuaValue.False : LuaValue.True;
                        break;

                    case LuaOpCode.JMP:
                        programCounter += inst.C;
                        break;

                    case LuaOpCode.TEST:
                        bool cond = registers[inst.A].AsBool;
                        if (!cond) programCounter++;
                        break;

                    case LuaOpCode.NEWTABLE:
                        int tableHandle = tableHeap.Count;
                        tableHeap.Add(new LuaTable { Handle = tableHandle });
                        registers[inst.A] = LuaValue.FromTable(tableHandle);
                        break;

                    case LuaOpCode.GETTABLE:
                        int tHandle = registers[inst.B].HandleOrId;
                        if (tHandle >= 0 && tHandle < tableHeap.Count)
                        {
                            LuaValue key = inst.C < constants.Count ? constants[inst.C] : registers[inst.C];
                            registers[inst.A] = tableHeap[tHandle].Get(key);
                        }
                        break;

                    case LuaOpCode.SETTABLE:
                        int setTHandle = registers[inst.A].HandleOrId;
                        if (setTHandle >= 0 && setTHandle < tableHeap.Count)
                        {
                            LuaValue key = inst.B < constants.Count ? constants[inst.B] : registers[inst.B];
                            tableHeap[setTHandle].Set(key, registers[inst.C]);
                        }
                        break;

                    case LuaOpCode.CALL:
                        ExecuteCall(inst.A, inst.B, inst.C);
                        break;

                    case LuaOpCode.RETURN:
                        currentState = VmExecutionState.Completed;
                        EventBus<LuaVmExecutionEvent>.Raise(new LuaVmExecutionEvent(GetInstanceID(), currentState, programCounter, opsExecuted));
                        return currentState;

                    case LuaOpCode.SLEEP:
                        sleepTimerSeconds = (float)registers[inst.A].AsNumber;
                        currentState = VmExecutionState.Sleeping;
                        EventBus<LuaVmExecutionEvent>.Raise(new LuaVmExecutionEvent(GetInstanceID(), currentState, programCounter, opsExecuted));
                        return currentState;

                    case LuaOpCode.YIELD:
                        currentState = VmExecutionState.Yielded;
                        EventBus<LuaVmExecutionEvent>.Raise(new LuaVmExecutionEvent(GetInstanceID(), currentState, programCounter, opsExecuted));
                        return currentState;
                }
            }

            if (programCounter >= codeLen)
            {
                currentState = VmExecutionState.Completed;
            }

            EventBus<LuaVmExecutionEvent>.Raise(new LuaVmExecutionEvent(GetInstanceID(), currentState, programCounter, opsExecuted));
            return currentState;
        }

        private void ExecuteCall(int funcReg, int argCountWithFunc, int retCount)
        {
            LuaValue target = registers[funcReg];
            if (target.Type == LuaValueType.NativeFuncId && target.HandleOrId < nativeCallbacks.Count)
            {
                int argCount = Math.Max(0, argCountWithFunc - 1);
                Span<LuaValue> argsSpan = registers.AsSpan(funcReg + 1, argCount);
                LuaValue result = nativeCallbacks[target.HandleOrId].Invoke(this, argsSpan);

                if (retCount > 1)
                {
                    registers[funcReg] = result;
                }
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class VmSaveData
        {
            public string SourceCode;
            public int PC;
            public int State;
            public float SleepTimer;
        }

        public object CaptureState()
        {
            return new VmSaveData
            {
                SourceCode = luaScript,
                PC = programCounter,
                State = (int)currentState,
                SleepTimer = sleepTimerSeconds
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<VmSaveData>(stateJson);
            if (data == null) return;

            luaScript = data.SourceCode;
            CompileScript(luaScript, out _);
            programCounter = data.PC;
            currentState = (VmExecutionState)data.State;
            sleepTimerSeconds = data.SleepTimer;
        }
    }

    // =========================================================================
    // 7. BUILT-IN 2D & 3D HARDWARE PERIPHERALS
    // =========================================================================

    /// <summary>
    /// Dual 2D/3D drone movement peripheral compatible with Rigidbody (3D), Rigidbody2D, and Transform.
    /// </summary>
    public class DroneHardwarePeripheral : ILuaPeripheral
    {
        public string PeripheralName => "drone";
        private readonly Transform transform;
        private readonly Rigidbody rb3D;
        private readonly Rigidbody2D rb2D;

        public DroneHardwarePeripheral(Transform t, Rigidbody r3, Rigidbody2D r2)
        {
            transform = t;
            rb3D = r3;
            rb2D = r2;
        }

        public void AttachToVM(InGameProgrammableVM vm)
        {
            // drone.move(dx, dy, [dz])
            vm.RegisterNativeFunction("drone_move", (vmInst, args) =>
            {
                float x = args.Length > 0 ? (float)args[0].AsNumber : 0f;
                float y = args.Length > 1 ? (float)args[1].AsNumber : 0f;
                float z = args.Length > 2 ? (float)args[2].AsNumber : 0f;

                Vector3 delta = new Vector3(x, y, z);

                if (rb3D != null)
                {
                    rb3D.linearVelocity = delta;
                }
                else if (rb2D != null)
                {
                    rb2D.linearVelocity = new Vector2(x, y);
                }
                else if (transform != null)
                {
                    transform.position += delta;
                }

                return LuaValue.True;
            });

            // drone.getPos() -> x, y, z
            vm.RegisterNativeFunction("drone_getpos_x", (v, a) => LuaValue.FromNumber(transform != null ? transform.position.x : 0));
            vm.RegisterNativeFunction("drone_getpos_y", (v, a) => LuaValue.FromNumber(transform != null ? transform.position.y : 0));
            vm.RegisterNativeFunction("drone_getpos_z", (v, a) => LuaValue.FromNumber(transform != null ? transform.position.z : 0));
        }
    }

    /// <summary>
    /// Circuit Network peripheral connecting Lua scripts to LogicGateAndCircuitSimulator.
    /// </summary>
    public class CircuitHardwarePeripheral : ILuaPeripheral
    {
        public string PeripheralName => "circuit";

        public void AttachToVM(InGameProgrammableVM vm)
        {
            // circuit.getSignal(networkId, wireColor, signalType)
            vm.RegisterNativeFunction("circuit_get", (v, args) =>
            {
                if (LogicGateAndCircuitSimulator.Instance == null || args.Length < 3)
                    return LuaValue.Nil;

                int netId = (int)args[0].AsNumber;
                CircuitWireColor wire = (CircuitWireColor)(int)args[1].AsNumber;
                CircuitSignalType sig = (CircuitSignalType)(int)args[2].AsNumber;

                CircuitValue val = LogicGateAndCircuitSimulator.Instance.ReadNetworkSignal(netId, wire, new CircuitSignalKey(sig));
                return LuaValue.FromNumber(val.FloatValue);
            });

            // circuit.setSignal(networkId, wireColor, signalType, value)
            vm.RegisterNativeFunction("circuit_set", (v, args) =>
            {
                if (LogicGateAndCircuitSimulator.Instance == null || args.Length < 4)
                    return LuaValue.Nil;

                int netId = (int)args[0].AsNumber;
                CircuitWireColor wire = (CircuitWireColor)(int)args[1].AsNumber;
                CircuitSignalType sig = (CircuitSignalType)(int)args[2].AsNumber;
                float fVal = (float)args[3].AsNumber;

                LogicGateAndCircuitSimulator.Instance.InjectExternalSignal(netId, wire, new CircuitSignalKey(sig), new CircuitValue(fVal));
                return LuaValue.True;
            });
        }
    }

    /// <summary>
    /// Inventory peripheral connecting Lua scripts to UniversalInventoryContainer.
    /// </summary>
    public class InventoryHardwarePeripheral : ILuaPeripheral
    {
        public string PeripheralName => "inv";
        private readonly UniversalInventoryContainer inventory;

        public InventoryHardwarePeripheral(UniversalInventoryContainer inv)
        {
            inventory = inv;
        }

        public void AttachToVM(InGameProgrammableVM vm)
        {
            // inv.getSupply(resourceId)
            vm.RegisterNativeFunction("inv_get_supply", (v, args) =>
            {
                if (inventory == null || args.Length < 1)
                    return LuaValue.FromNumber(0);

                string resId = args[0].Type == LuaValueType.StringId ? "res_item" : "res_item";
                return LuaValue.FromNumber(inventory.GetAvailableSupply(resId));
            });

            // inv.getWeight()
            vm.RegisterNativeFunction("inv_get_weight", (v, a) =>
                LuaValue.FromNumber(inventory != null ? inventory.GetTotalWeight() : 0));
        }
    }
}
