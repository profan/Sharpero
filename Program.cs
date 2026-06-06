using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Raylib_cs;
using SkiaSharp;

Main();

const string programsProsperoVm = "Programs/prospero.vm";

// STAThread is required if you deploy using NativeAOT on Windows
// See https://github.com/raylib-cs/raylib-cs/issues/301
[STAThread]
void Main()
{
    string fragmentShader = """
        #version 330

        in vec2 fragTexCoord;
        in vec4 fragColor;

        uniform sampler2D texture0;

        out vec4 outputColor;

        void main()
        {
            float v = texture(texture0, fragTexCoord).r < 0 ? 1.0 : 0.0;
            outputColor = vec4(v, v, v, 1.0f) * fragColor;
        }
    """;
    
    int currentOutputImageSize = 1024;
    Raylib.InitWindow(currentOutputImageSize, currentOutputImageSize, "Sharpero");

    Texture2D currentOutputTexture;
    Shader currentShader = Raylib.LoadShaderFromMemory(null, fragmentShader);
    float[] currentOutputImageData = new float[currentOutputImageSize * currentOutputImageSize];
    
    unsafe
    {
        fixed (float* currentOutputImageDataPtr = currentOutputImageData)
        {
            Image currentOutputImage = new Image()
            {
                Format = PixelFormat.UncompressedR32,
                Data = currentOutputImageDataPtr,
                Width = currentOutputImageSize,
                Height = currentOutputImageSize,
                Mipmaps = 1
            };
        
            currentOutputTexture = Raylib.LoadTextureFromImage(currentOutputImage);
        }
    }
   
    // options??
    bool shouldUseCompiler = false;
    bool shouldUseParallelism = true;
    bool shouldUseSimd = true;

    bool isEvaluating = false;
    bool shouldEvaluate = false;
    bool shouldCancelUpdateTexture = false;
    bool shouldUpdateTexture = false;

    float lastEvaluationTimeTook = 0.0f;

    while (!Raylib.WindowShouldClose()) 
    {
        Raylib.BeginDrawing();
        
        Raylib.ClearBackground(Color.White);

        if (shouldEvaluate && isEvaluating)
        {
            shouldEvaluate = false;
        }

        if (shouldEvaluate && !isEvaluating)
        {
            isEvaluating = true;
            shouldUpdateTexture = true;
            InterpreterOptions interpreterOptions = (shouldUseParallelism ? InterpreterOptions.Parallelism : default)
                                                    | (shouldUseSimd ? InterpreterOptions.Simd : default)
                                                    | (shouldUseCompiler
                                                        ? InterpreterOptions.CompileInnerLoop
                                                        : default);
            
            Task.Run(() =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                EvaluationInstructions instructions = Parsing.Parse(programsProsperoVm);
                currentOutputImageData.AsSpan()[..currentOutputImageData.Length].Clear();

                if ((interpreterOptions & InterpreterOptions.Simd) != 0)
                {
                    Interpreter.Evaluate<Vector<float>>(instructions, imageSize: currentOutputImageSize, interpreterOptions, currentOutputImageData);
                }
                else
                {
                    Interpreter.Evaluate<float>(instructions, imageSize: currentOutputImageSize, interpreterOptions, currentOutputImageData);
                }

                lastEvaluationTimeTook = (float)sw.Elapsed.TotalSeconds;
                Raylib.UpdateTexture(currentOutputTexture, currentOutputImageData);
                shouldCancelUpdateTexture = true;
                isEvaluating = false;
            });
            
            shouldEvaluate = false;
        }

        if (shouldUpdateTexture)
        {
            Raylib.UpdateTexture(currentOutputTexture, currentOutputImageData);
            if (shouldCancelUpdateTexture)
            {
                shouldCancelUpdateTexture = false;
                shouldUpdateTexture = false;
            }
        }
        
        Raylib.BeginShaderMode(currentShader);
            Raylib.DrawTexture(currentOutputTexture, 0, 0, Color.White);
        Raylib.EndShaderMode();
        
        Raylib.DrawText("Sharpero (press R to evaluate, O to output to file)", 12, 12, 20, Color.White);
        Raylib.DrawText($" - parallelism {(shouldUseParallelism ? "enabled" : "disabled")} (P to toggle), simd {(shouldUseSimd ? "enabled" : "disabled")} (S to toggle), compile {(shouldUseCompiler? "enabled" : "disabled")} (C to toggle)", 12, 32, 20, Color.White);

        if (lastEvaluationTimeTook != 0.0f)
        {
            double evaluationTimeNanoSeconds = (lastEvaluationTimeTook * 1000.0 * 1000.0 * 1000.0);
            double nanoSecondsPerPixel = evaluationTimeNanoSeconds / (currentOutputImageSize * currentOutputImageSize);
            Raylib.DrawText($" - evaluation took: {lastEvaluationTimeTook:0.0} s ({nanoSecondsPerPixel:0.0} ns / pixel)", 12, 52, 20, Color.White);
        }

        if (Raylib.IsKeyPressed(KeyboardKey.R))
        {
            shouldEvaluate = true;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.O))
        {
            Task.Run(() =>
            {
                GenerateOutputImage(currentImageSize: currentOutputImageSize, shouldWriteOutputImage: true);
            });
        }

        if (Raylib.IsKeyPressed(KeyboardKey.P))
        {
            shouldUseParallelism = !shouldUseParallelism;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.S))
        {
            shouldUseSimd = !shouldUseSimd;
        }
        
        if (Raylib.IsKeyPressed(KeyboardKey.C))
        {
            shouldUseCompiler = !shouldUseCompiler;
        }

        Raylib.EndDrawing();
    } 
    
    Raylib.UnloadShader(currentShader);
    Raylib.UnloadTexture(currentOutputTexture);
    Raylib.CloseWindow();
}

float[] GenerateOutputImage(int currentImageSize, bool shouldWriteOutputImage = false)
{
    string combinedOutputString = string.Empty;

    (float[] result, double totalTimeTakenSecondsOutput) = BenchmarkFunction(() =>
    {
        (float[] result, double timeTakenSecondsEvaluate) = BenchmarkFunction(() =>
        {
            EvaluationInstructions instructions = Parsing.Parse(programsProsperoVm);
            InterpreterOptions interpreterOptions = InterpreterOptions.Parallelism | InterpreterOptions.Simd;
            return Interpreter.Evaluate<Vector<float>>(instructions, imageSize: currentImageSize, interpreterOptions);
        });

        combinedOutputString += $" - took: {timeTakenSecondsEvaluate} seconds to evaluate the image!" + Environment.NewLine;

        if (shouldWriteOutputImage)
        {
            (bool success, double timeTakenSecondsOutput) = BenchmarkFunction(() =>
            {
                WriteOutputImage(currentImageSize, result, "prospero.jpg");
                return true;
            });
            
            combinedOutputString += $" - took: {timeTakenSecondsOutput} seconds to write out image!" + Environment.NewLine;
        }

        return result;
    });

    Console.Write($"Sharpero took: {totalTimeTakenSecondsOutput} seconds to evaluate {currentImageSize}x{currentImageSize} image!" + Environment.NewLine + combinedOutputString);

    (T, double) BenchmarkFunction<T>(Func<T> benchmarkedFunction)
    {
        var sw = Stopwatch.StartNew();
        T benchmarkedResult = benchmarkedFunction();
        return (benchmarkedResult, sw.Elapsed.TotalSeconds);
    }

    void WriteOutputImage(int imageSize, float[] imageData, string imageOutputPath)
    {
        byte[] imageDataBytes = imageData.Select(p => (byte)(p < 0 ? 255 : 0)).ToArray();
        using var image = SKImage.FromPixelCopy(new SKImageInfo(imageSize, imageSize, SKColorType.Gray8), imageDataBytes);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        using var stream = File.OpenWrite(imageOutputPath);
        data.SaveTo(stream);
    }

    return result;
}

internal enum OpCode { VarX, VarY, Const, Add, Sub, Mul, Max, Min, Neg, Square, Sqrt }

internal readonly record struct Operand
{
    private readonly int _value = -1;

    public bool IsConstant => ((_value >> 31) & 1) != 0;
    public int Value => _value & 0x7FFFFFFF;
    
    public Operand(int value, bool isConstant = false)
    {
        _value = (isConstant ? 1 : 0) << 31 | value & 0x7FFFFFFF;
    }

    public static implicit operator int(Operand o) => o.Value;
    public override string ToString() => $"{(IsConstant ? Value : $"_{Value}")}";
}

internal readonly record struct Instruction(
    Operand Out,
    OpCode OpCode,
    Operand A = default,
    Operand B = default,
    float C = 0.0f)
{
    public override string ToString()
    {
        return OpCode switch
        {
            OpCode.VarX => $"_{Out} var-x",
            OpCode.VarY => $"_{Out} var-y",
            OpCode.Const => $"_{Out} const",
            OpCode.Add => $"_{Out} add {A} {B}",
            OpCode.Sub => $"_{Out} sub {A} {B}",
            OpCode.Mul => $"_{Out} mul {A} {B}",
            OpCode.Max => $"_{Out} max {A} {B}",
            OpCode.Min => $"_{Out} min {A} {B}",
            OpCode.Neg => $"_{Out} neg {A}",
            OpCode.Square => $"_{Out} square {A}",
            OpCode.Sqrt => $"_{Out} sqrt {A}",
            _ => string.Empty
        };
    }
}

internal record EvaluationInstructions(Instruction[] Instructions);

internal static class Parsing
{

    // 1D <-> 2D coordinate helpers for square grids
    public static (int x, int y) IndexToCoord(int idx, int width) => (idx % width, idx / width);
    public static int CoordToIndex(int x, int y, int width) => x + (y * width);

    // the identifiers are hexadecimal, stripping off the leading _ is enough :),
    public static Operand ParseIdentifier(Dictionary<string, Operand> identifiers, string id, bool isConstant = false)
    {
        if (identifiers.TryGetValue(id, out var value))
        {
            return value;
        }

        return identifiers[id] = new Operand(Convert.ToInt32(id[1..], 16), isConstant);
    }

    private static float EvaluateExpression(OpCode opCode, float a, float b)
    {
        return opCode switch
        {
            OpCode.Add => a + b,
            OpCode.Sub => a - b,
            OpCode.Mul => a * b,
            _ => 0.0f
        };
    }
    
    public static EvaluationInstructions Parse(string filename)
    {
        var identifiers = new Dictionary<string, Operand>();
        foreach (string line in File.ReadAllLines(filename))
        {
            switch (line.Split(" "))
            {
                case [{ } @out, "const", not null]:
                    ParseIdentifier(identifiers, @out, isConstant: true);
                    break;
                case [{ } @out, not null, { } a, { } b]:
                    ParseIdentifier(identifiers, @out);
                    ParseIdentifier(identifiers, a);
                    ParseIdentifier(identifiers, b);
                    break;
                case [{ } @out, not null, { } a]:
                    ParseIdentifier(identifiers, @out);
                    ParseIdentifier(identifiers, a);
                    break;
                case [{ } @out, not null]:
                    ParseIdentifier(identifiers, @out);
                    break;
            }
        }

        List<Instruction> instructions = [];
        foreach (string line in File.ReadAllLines(filename))
        {
            Instruction? parsedInstruction = line.Split(" ") switch
            {
                [{ } @out, "var-x"] => new Instruction(identifiers[@out], OpCode.VarX),
                [{ } @out, "var-y"] => new Instruction(identifiers[@out], OpCode.VarY),
                [{ } @out, "const", { } v]=> new Instruction(identifiers[@out], OpCode.Const, C: float.Parse(v, CultureInfo.InvariantCulture)),
                [{ } @out, "add", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Add, identifiers[a], identifiers[b]),
                [{ } @out, "sub", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Sub, identifiers[a], identifiers[b]),
                [{ } @out, "mul", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Mul, identifiers[a], identifiers[b]),
                [{ } @out, "max", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Max, identifiers[a], identifiers[b]),
                [{ } @out, "min", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Min, identifiers[a], identifiers[b]),
                [{ } @out, "neg", { } a] => new Instruction(identifiers[@out], OpCode.Neg, identifiers[a]),
                [{ } @out, "square", { } a] => new Instruction(identifiers[@out], OpCode.Square, identifiers[a]),
                [{ } @out, "sqrt", { } a] => new Instruction(identifiers[@out], OpCode.Sqrt, identifiers[a]),
                _ => null
            };

            if (parsedInstruction is { } instruction)
            {
                instructions.Add(instruction);
            }
        }

        bool shouldEliminateConstants = true;

        if (shouldEliminateConstants)
        {
            // handle constant propagation, eliminating all constants from the instruction stream and merging them
            foreach (ref Instruction instruction in CollectionsMarshal.AsSpan(instructions))
            {
                switch (instruction.OpCode)
                {
                    case (OpCode.Add or OpCode.Sub or OpCode.Mul) when instruction.A.IsConstant || instruction.B.IsConstant:
                        switch (instruction)
                        {
                            case { A: { IsConstant: true } a, B.IsConstant: false }:
                                instruction = instruction with { C = instructions[a].C };
                                break;
                            case { A.IsConstant: false, B: { IsConstant: true } b }:
                                instruction = instruction with { C = instructions[b].C };
                                break;
                            case { OpCode: var opCode, A: { IsConstant: true } a, B: { IsConstant: true } b }:
                                instruction = instruction with { C = EvaluateExpression(opCode, instructions[a].C, instructions[b].C) };
                                break;
                            case { A.IsConstant: false, B.IsConstant: false }:
                                break;
                        }
                        break;
                }
            }

            // relocate all offsets, now that we've nuked all the constants
            foreach (ref Instruction instruction in CollectionsMarshal.AsSpan(instructions))
            {
                switch (instruction.OpCode)
                {
                    case OpCode.Const:
                        int offset = instruction.Out;
                        foreach (ref Instruction otherInstruction in CollectionsMarshal.AsSpan(instructions))
                        {
                            int newOperandOut = otherInstruction.Out >= offset
                                ? otherInstruction.Out - 1
                                : otherInstruction.Out;
                            
                            int newOperandA = otherInstruction.A >= offset
                                ? otherInstruction.A - 1
                                : otherInstruction.A;

                            int newOperandB = otherInstruction.B >= offset
                                ? otherInstruction.B - 1
                                : otherInstruction.B;

                            if (newOperandOut == otherInstruction.Out
                                && newOperandA == otherInstruction.A
                                && newOperandB == otherInstruction.B)
                            {
                                continue;
                            }

                            otherInstruction = otherInstruction with
                            {
                                Out = new Operand(newOperandOut),
                                A = new Operand(newOperandA, isConstant: otherInstruction.A.IsConstant),
                                B = new Operand(newOperandB, isConstant: otherInstruction.B.IsConstant),
                            };
                        }
                        break;
                }
            }

            // eliminate all constants now :)
            int totalNumberOfInstructions = instructions.Count;
            int numberOfRemovedInstructions = instructions.RemoveAll(i => i.OpCode == OpCode.Const);
            Console.WriteLine($"Sharpero eliminated {numberOfRemovedInstructions} constants from tape, {(float)numberOfRemovedInstructions / totalNumberOfInstructions * 100.0f:0.0} % of total");
        }

        return new EvaluationInstructions(instructions.ToArray());
    }   
}

[Flags]
internal enum InterpreterOptions
{
    CompileInnerLoop = 0x1,
    Parallelism = 0x2,
    Simd = 0x4
}

internal static class Evaluation
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<T>(Span<T> variables, T value, int offset)
        where T : unmanaged
    {
        Unsafe.Add(ref MemoryMarshal.GetReference(variables), offset) = value;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Read<T>(Span<T> variables, int offset)
        where T : unmanaged
    {
        return Unsafe.Add(ref MemoryMarshal.GetReference(variables), offset);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Add<T>(T a, T b)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(Unsafe.As<T, float>(ref a) + Unsafe.As<T, float>(ref b));
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(Unsafe.As<T, Vector<float>>(ref a) + Unsafe.As<T, Vector<float>>(ref b));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
   
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Sub<T>(T a, T b)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(Unsafe.As<T, float>(ref a) - Unsafe.As<T, float>(ref b));
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(Unsafe.As<T, Vector<float>>(ref a) - Unsafe.As<T, Vector<float>>(ref b));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
  
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Mul<T>(T a, T b)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(Unsafe.As<T, float>(ref a) * Unsafe.As<T, float>(ref b));
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(Unsafe.As<T, Vector<float>>(ref a) * Unsafe.As<T, Vector<float>>(ref b));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
   
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Neg<T>(T v)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(-Unsafe.As<T, float>(ref v));
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(-Unsafe.As<T, Vector<float>>(ref v));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Max<T>(T a, T b)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(MathF.Max(Unsafe.As<T, float>(ref a), Unsafe.As<T, float>(ref b)));
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(Vector.Max(Unsafe.As<T, Vector<float>>(ref a), Unsafe.As<T, Vector<float>>(ref b)));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Min<T>(T a, T b)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(MathF.Min(Unsafe.As<T, float>(ref a), Unsafe.As<T, float>(ref b)));
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(Vector.Min(Unsafe.As<T, Vector<float>>(ref a), Unsafe.As<T, Vector<float>>(ref b)));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T SquareRoot<T>(T v)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(MathF.Sqrt(Unsafe.As<T, float>(ref v)));
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(Vector.SquareRoot(Unsafe.As<T, Vector<float>>(ref v)));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Square<T>(T v)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(Unsafe.As<T, float>(ref v) * Unsafe.As<T, float>(ref v));
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(Vector.Multiply(Unsafe.As<T, Vector<float>>(ref v), Unsafe.As<T, Vector<float>>(ref v)));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T EvaluateConstant<T>(float c)
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(c);
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(new Vector<float>(c));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T One<T>()
        where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(1.0f);
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(Vector<float>.One);
        }
        else
        {
            throw new InvalidOperationException();
        }
    }   
}

internal static class Delegates<T>
    where T : unmanaged
{
    public delegate void EvaluateDelegate(Span<T> variables, T xs, T ys);
}

internal static class Compiler
{
    internal static class CompilerCache<T>
        where T : unmanaged
    {
        internal static readonly ConcurrentDictionary<EvaluationInstructions, Delegates<T>.EvaluateDelegate> CachedPrograms =
            new ConcurrentDictionary<EvaluationInstructions, Delegates<T>.EvaluateDelegate>();
    }
    
    private static (AssemblyBuilder, MethodBuilder, TypeBuilder) CreateDynamicAssemblyWithMethodBuilder<T>(string assemblyName, string moduleName, string typeName, string methodName)
        where T : unmanaged
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(moduleName);
        var typeBuilder = moduleBuilder.DefineType(typeName, TypeAttributes.Public);
        var methodBuilder =
            typeBuilder.DefineMethod(
                methodName,
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(void),
                [typeof(Span<T>), typeof(T), typeof(T)]);
        
        return (assemblyBuilder, methodBuilder, typeBuilder);
    }
    
    public static Delegates<T>.EvaluateDelegate Compile<T>(EvaluationInstructions evaluationInstructions)
        where T : unmanaged
    {
        if (CompilerCache<T>.CachedPrograms.TryGetValue(evaluationInstructions, out var program))
        {
            return program;
        }

        lock (CompilerCache<T>.CachedPrograms)
        {
            int maximumInstructionsPerChunk = 32;
                
            Stopwatch totalSw = Stopwatch.StartNew();
            var chunkDelegates = evaluationInstructions.Instructions
                .Chunk(maximumInstructionsPerChunk)
                .AsParallel()
                .Select((instructionsChunk, instructionChunkIndex) => 
                {
                    Stopwatch subTotalSw = Stopwatch.StartNew();
                    var evaluateDynamicMethod = new DynamicMethod(
                        $"EvaluateChunk_{instructionChunkIndex}",
                        typeof(void),
                        [typeof(Span<T>), typeof(T), typeof(T)],
                        typeof(Compiler).Module,
                        skipVisibility: true
                    );
                    
                    var methodGenerator = evaluateDynamicMethod.GetILGenerator();
                    methodGenerator.DeclareLocal(typeof(T));

                    foreach (Instruction instruction in instructionsChunk)
                    {
                        CompileInstruction<T>(methodGenerator, in instruction);
                    }
                    
                    // finalize method, we need a return as our final bit
                    methodGenerator.Emit(OpCodes.Ret);
                    
                    // create the delegate, but also force the JIT to compile our function :D
                    var newChunkDelegate = evaluateDynamicMethod.CreateDelegate<Delegates<T>.EvaluateDelegate>();
                    RuntimeHelpers.PrepareDelegate(newChunkDelegate);
                    
                    Console.WriteLine($" - took: {subTotalSw.Elapsed.Milliseconds:0.0} ms to compile sub program ({instructionChunkIndex}) ({methodGenerator.ILOffset} bytes of instructions)!");
                    return newChunkDelegate;
                }).ToList();

            Console.WriteLine($"Sharpero took: {totalSw.Elapsed.Milliseconds:0.0} ms to compile program with {chunkDelegates.Count} parts (max instructions per chunk = {maximumInstructionsPerChunk})!");
            CompilerCache<T>.CachedPrograms[evaluationInstructions] = (variables, xs, ys) =>
            {
                foreach (Delegates<T>.EvaluateDelegate chunkDelegate in chunkDelegates)
                {
                    chunkDelegate(variables, xs, ys);
                }
            };
           
            // force the JIT to compile our function :D
            RuntimeHelpers.PrepareDelegate(CompilerCache<T>.CachedPrograms[evaluationInstructions]);
            return CompilerCache<T>.CachedPrograms[evaluationInstructions];
        }
    }

    private static void CompileInstruction<T>(ILGenerator methodGenerator, in Instruction instruction)
        where T : unmanaged
    {
        void EmitReadConstant(float constant)
        {
            methodGenerator.Emit(OpCodes.Ldc_R4, constant); // constant 
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(nameof(Evaluation.EvaluateConstant))!.MakeGenericMethod(typeof(T)));
        }

        void EmitWriteConstant(int offset, float constant)
        {
            // write, but with constant value 
            methodGenerator.Emit(OpCodes.Ldarg_0); // Span<T>
            methodGenerator.Emit(OpCodes.Ldc_R4, constant); // result of call
            methodGenerator.Emit(OpCodes.Ldc_I4, offset); // offset
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(nameof(Evaluation.Write))!.MakeGenericMethod(typeof(T)));
        }

        void EmitWrite(int offset)
        {
            // write, previous result will be at loc 0 if all is well
            methodGenerator.Emit(OpCodes.Ldarg_0); // Span<T>
            methodGenerator.Emit(OpCodes.Ldloc_0); // result of call
            methodGenerator.Emit(OpCodes.Ldc_I4, offset); // offset
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(nameof(Evaluation.Write))!.MakeGenericMethod(typeof(T)));
        }

        void EmitWriteXs(int offset)
        {
            // write, but xs specifically
            methodGenerator.Emit(OpCodes.Ldarg_0); // Span<T>
            methodGenerator.Emit(OpCodes.Ldarg_1); // xs
            methodGenerator.Emit(OpCodes.Ldc_I4, offset); // offset
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(nameof(Evaluation.Write))!.MakeGenericMethod(typeof(T)));
        }

        void EmitWriteYs(int offset)
        {
            // write, but ys specifically
            methodGenerator.Emit(OpCodes.Ldarg_0); // Span<T>
            methodGenerator.Emit(OpCodes.Ldarg_2); // ys
            methodGenerator.Emit(OpCodes.Ldc_I4, offset); // offset
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(nameof(Evaluation.Write))!.MakeGenericMethod(typeof(T)));
        }
        
        void EmitInvokeUnaryOperation(string methodName)
        {
            // call with Span<T>, pushed results of variables[a] and variables[b] reads
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(methodName)!.MakeGenericMethod(typeof(T)));
            methodGenerator.Emit(OpCodes.Stloc_0);
        }

        void EmitInvokeBinaryOperation(string methodName)
        {
            // call with Span<T>, pushed results of variables[a] and variables[b] reads
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(methodName)!.MakeGenericMethod(typeof(T)));
            methodGenerator.Emit(OpCodes.Stloc_0);
        }

        void EmitReadVariablesB(Operand b)
        {
            methodGenerator.Emit(OpCodes.Ldarg_0); // Span<T>
            methodGenerator.Emit(OpCodes.Ldc_I4, b); // b
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(nameof(Evaluation.Read))!.MakeGenericMethod(typeof(T)));
        }

        void EmitReadVariablesA(Operand a)
        {
            methodGenerator.Emit(OpCodes.Ldarg_0); // Span<T>
            methodGenerator.Emit(OpCodes.Ldc_I4, a); // a
            methodGenerator.Emit(OpCodes.Call, typeof(Evaluation).GetMethod(nameof(Evaluation.Read))!.MakeGenericMethod(typeof(T)));
        }

        switch (instruction)
        {
            case { OpCode: OpCode.VarX }: // => xs,
                
                EmitWriteXs(instruction.Out);
                break;
            
            case { OpCode: OpCode.VarY }: // => ys,

                EmitWriteYs(instruction.Out);
                break;
            
            case { OpCode: OpCode.Add, A: { IsConstant: false } a, B: { IsConstant: false } b }: // => Add(variables[a], variables[b]),
                
                EmitReadVariablesA(a);
                EmitReadVariablesB(b);
                EmitInvokeBinaryOperation(nameof(Evaluation.Add));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Add, A.IsConstant: true, B: { IsConstant: false } b }: // => Add(EvaluateConstant<T>(instruction.C), variables[b]),
                
                EmitReadConstant(instruction.C);
                EmitReadVariablesB(b);
                EmitInvokeBinaryOperation(nameof(Evaluation.Add));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Add, A: { IsConstant: false } a, B.IsConstant: true }: // => Add(variables[a], EvaluateConstant<T>(instruction.C)),
               
                EmitReadVariablesA(a);
                EmitReadConstant(instruction.C);
                EmitInvokeBinaryOperation(nameof(Evaluation.Add));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Sub, A: { IsConstant: false } a, B: { IsConstant: false } b }: // => Sub(variables[a], variables[b]),
                
                EmitReadVariablesA(a);
                EmitReadVariablesB(b);
                EmitInvokeBinaryOperation(nameof(Evaluation.Sub));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Sub, A.IsConstant: true, B: { IsConstant: false } b }: // => Sub(EvaluateConstant<T>(instruction.C), variables[b]),
               
                EmitReadConstant(instruction.C);
                EmitReadVariablesB(b);
                EmitInvokeBinaryOperation(nameof(Evaluation.Sub));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Sub, A: { IsConstant: false } a, B.IsConstant: true }: // => Sub(variables[a], EvaluateConstant<T>(instruction.C)),
               
                EmitReadVariablesA(a);
                EmitReadConstant(instruction.C);
                EmitInvokeBinaryOperation(nameof(Evaluation.Sub));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Mul, A: { IsConstant: false } a, B: { IsConstant: false } b }: // => Mul(variables[a], variables[b]),
               
                EmitReadVariablesA(a);
                EmitReadVariablesB(b); 
                EmitInvokeBinaryOperation(nameof(Evaluation.Mul));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Mul, A.IsConstant: true, B: { IsConstant: false } b }: // => Mul(EvaluateConstant<T>(instruction.C), variables[b]),
                
                EmitReadConstant(instruction.C);
                EmitReadVariablesB(b);
                EmitInvokeBinaryOperation(nameof(Evaluation.Mul));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Mul, A: { IsConstant: false } a, B.IsConstant: true }: // => Mul(variables[a], EvaluateConstant<T>(instruction.C)),
                
                EmitReadVariablesA(a);
                EmitReadConstant(instruction.C);
                EmitInvokeBinaryOperation(nameof(Evaluation.Mul));
                EmitWrite(instruction.Out); 
                break;
            
            case { OpCode: OpCode.Max, A: var a, B: var b }: // => Max(variables[a], variables[b]),
               
                EmitReadVariablesA(a);
                EmitReadVariablesB(b);
                EmitInvokeBinaryOperation(nameof(Evaluation.Max));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Min, A: var a, B: var b }: // => Min(variables[a], variables[b]),
                
                EmitReadVariablesA(a);
                EmitReadVariablesB(b);
                EmitInvokeBinaryOperation(nameof(Evaluation.Min));
                EmitWrite(instruction.Out);
                break;               
            
            case { OpCode: OpCode.Neg, A: var a }: // => Neg(variables[a]),
               
                EmitReadVariablesA(a);
                EmitInvokeUnaryOperation(nameof(Evaluation.Neg));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Sqrt, A: var a }: // => SquareRoot(variables[a]),
                
                EmitReadVariablesA(a);
                EmitInvokeUnaryOperation(nameof(Evaluation.SquareRoot));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Square, A: var a }: // => Square(variables[a]),
                
                EmitReadVariablesA(a);
                EmitInvokeUnaryOperation(nameof(Evaluation.Square));
                EmitWrite(instruction.Out);
                break;
            
            case { OpCode: OpCode.Const, C: var v }: // => EvaluateConstant<T>(v),
                
                EmitWriteConstant(instruction.Out, v);
                break;
        }
    }
}

internal static class Interpreter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T GetValues<T>(Span<float> values)
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.BitCast<float, T>(values[0]);
        }
        else if (typeof(T) == typeof(Vector<float>))
        {
            return Unsafe.BitCast<Vector<float>, T>(new Vector<float>(values));
        }
        else
        {
            throw new InvalidOperationException();
        }
    }
   
    [SkipLocalsInit]
    public static float[] Evaluate<T>(EvaluationInstructions evaluationInstructions, int imageSize, InterpreterOptions options = default, float[]? result = null)
        where T : unmanaged
    {
        result ??= new float[imageSize * imageSize];

        ParallelOptions parallelOptions = new ParallelOptions()
        {
            MaxDegreeOfParallelism = (options & InterpreterOptions.Parallelism) != 0 ? -1 : 1
        };
        
        bool shouldCompileInnerLoop = (options & InterpreterOptions.CompileInnerLoop) != 0;

        if (shouldCompileInnerLoop)
        {
            Compiler.Compile<T>(evaluationInstructions);
        }

        int chunkSize = typeof(T) == typeof(Vector<float>) ? Vector<float>.Count : 1;
        Parallel.For(0, (imageSize * imageSize) / chunkSize, parallelOptions, chunkIdx =>
        {
            Span<float> xs = stackalloc float[chunkSize];
            Span<float> ys = stackalloc float[chunkSize];
            for (int idx = 0; idx < chunkSize; ++idx)
            {
                int currentIdx = chunkIdx * chunkSize + idx;
                (int x, int y) = Parsing.IndexToCoord(currentIdx, width: imageSize);

                // fix up the coordinate space, our space is actually more like [-imageSize * 0.5f, imageSize * 0.5f],
                // ... rather than [0, imageSize] in x/y, so this gives us the expected result
                float vx = (x / (imageSize * 0.5f)) - 1.0f;
                float vy = 1.0f - (y / (imageSize * 0.5f));

                (xs[idx], ys[idx]) = (vx, vy);
            }

            T results = shouldCompileInnerLoop
                ? EvaluateCompiled(evaluationInstructions,GetValues<T>(xs), GetValues<T>(ys))
                : EvaluateInterpreted(evaluationInstructions, GetValues<T>(xs), GetValues<T>(ys));
            
            for (int idx = 0; idx < chunkSize; ++idx)
            {
                int currentIdx = chunkIdx * chunkSize + idx;
                (int x, int y) = Parsing.IndexToCoord(currentIdx, width: imageSize);
                result[Parsing.CoordToIndex(x, y, width: imageSize)] = Unsafe.Add(ref Unsafe.As<T, float>(ref results), idx);
            }
        });

        return result;
    }

    [SkipLocalsInit]
    public static T EvaluateCompiled<T>(EvaluationInstructions evaluationInstructions, T xs, T ys)
        where T : unmanaged
    {
        // #TODO: this construction is just a little bit unhinged lol
        Span<T> variables = stackalloc T[evaluationInstructions.Instructions.Length];
       
        // compile if we've not already cached this instruction set, and evaluate!
        Compiler.Compile<T>(evaluationInstructions)?.Invoke(variables, xs, ys);
        
        return variables[evaluationInstructions.Instructions.Length - 1];
    }
    
    [SkipLocalsInit]
    public static T EvaluateInterpreted<T>(EvaluationInstructions evaluationInstructions, T xs, T ys)
        where T : unmanaged 
    {
        // #TODO: this construction is just a little bit unhinged lol
        Span<T> variables = stackalloc T[evaluationInstructions.Instructions.Length];
        
        foreach (ref Instruction instruction in evaluationInstructions.Instructions.AsSpan())
        {
            variables[instruction.Out] = instruction switch
            {
                { OpCode: OpCode.VarX } => xs,
                { OpCode: OpCode.VarY } => ys,
                
                { OpCode: OpCode.Add, A: { IsConstant: false } a, B: { IsConstant: false } b } => Evaluation.Add(variables[a], variables[b]),
                { OpCode: OpCode.Add, A.IsConstant: true, B: { IsConstant: false } b } => Evaluation.Add(Evaluation.EvaluateConstant<T>(instruction.C), variables[b]),
                { OpCode: OpCode.Add, A: { IsConstant: false } a, B.IsConstant: true } => Evaluation.Add(variables[a], Evaluation.EvaluateConstant<T>(instruction.C)),
                
                { OpCode: OpCode.Sub, A: { IsConstant: false } a, B: { IsConstant: false } b } => Evaluation.Sub(variables[a], variables[b]),
                { OpCode: OpCode.Sub, A.IsConstant: true, B: { IsConstant: false } b } => Evaluation.Sub(Evaluation.EvaluateConstant<T>(instruction.C), variables[b]),
                { OpCode: OpCode.Sub, A: { IsConstant: false } a, B.IsConstant: true } => Evaluation.Sub(variables[a], Evaluation.EvaluateConstant<T>(instruction.C)),
                
                { OpCode: OpCode.Mul, A: { IsConstant: false } a, B: { IsConstant: false } b } => Evaluation.Mul(variables[a], variables[b]),
                { OpCode: OpCode.Mul, A.IsConstant: true, B: { IsConstant: false } b } => Evaluation.Mul(Evaluation.EvaluateConstant<T>(instruction.C), variables[b]),
                { OpCode: OpCode.Mul, A: { IsConstant: false } a, B.IsConstant: true } => Evaluation.Mul(variables[a], Evaluation.EvaluateConstant<T>(instruction.C)),
                
                { OpCode: OpCode.Max, A: var a, B: var b } => Evaluation.Max(variables[a], variables[b]),
                { OpCode: OpCode.Min, A: var a, B: var b } => Evaluation.Min(variables[a], variables[b]),
                { OpCode: OpCode.Neg, A: var a } => Evaluation.Neg(variables[a]),
                { OpCode: OpCode.Sqrt, A: var a } => Evaluation.SquareRoot(variables[a]),
                { OpCode: OpCode.Square, A: var a } => Evaluation.Square(variables[a]),
                
                { OpCode: OpCode.Const, C: var v } => Evaluation.EvaluateConstant<T>(v),
                _ => variables[instruction.Out]
            };
        }

        return variables[evaluationInstructions.Instructions.Length - 1];
    }
}