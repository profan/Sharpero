using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
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
    
    bool shouldUseParallelism = true;
    bool shouldUseSimd = true;

    bool isEvaluating = false;
    bool shouldEvaluate = false;
    bool shouldCancelUpdateTexture = false;
    bool shouldUpdateTexture = false;

    while (!Raylib.WindowShouldClose()) 
    {
        Raylib.BeginDrawing();
        
        Raylib.ClearBackground(Color.White);

        if (shouldEvaluate & !isEvaluating)
        {
            shouldUpdateTexture = true;
            currentOutputImageData.AsSpan()[..currentOutputImageData.Length].Clear();

            InterpreterOptions interpreterOptions = (shouldUseParallelism ? InterpreterOptions.Parallelism : default);
            
            Task.Run(() =>
            {
                Instruction[] instructions = Parsing.Parse(programsProsperoVm);
                Interpreter.Evaluate(instructions, imageSize: currentOutputImageSize, interpreterOptions, currentOutputImageData);
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
        Raylib.DrawText($" - parallelism {(shouldUseParallelism ? "enabled" : "disabled")} (P to toggle)", 12, 32, 20, Color.White);

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
            Instruction[] instructions = Parsing.Parse(programsProsperoVm);
            return Interpreter.Evaluate(instructions, imageSize: currentImageSize);
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
    public override string ToString() => $"{Value} (constant: {IsConstant.ToString().ToLower()})";
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

    public static Instruction[] Parse(string filename)
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
                [{ } @out, "const", { } v] => new Instruction(identifiers[@out], OpCode.Const,
                    C: float.Parse(v, CultureInfo.InvariantCulture)),
                [{ } @out, "add", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Add, identifiers[a],
                    identifiers[b]),
                [{ } @out, "sub", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Sub, identifiers[a],
                    identifiers[b]),
                [{ } @out, "mul", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Mul, identifiers[a],
                    identifiers[b]),
                [{ } @out, "max", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Max, identifiers[a],
                    identifiers[b]),
                [{ } @out, "min", { } a, { } b] => new Instruction(identifiers[@out], OpCode.Min, identifiers[a],
                    identifiers[b]),
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

        float EvaluateExpression(OpCode opCode, float a, float b)
        {
            return opCode switch
            {
                OpCode.Add => a + b,
                OpCode.Sub => a - b,
                OpCode.Mul => a * b,
                _ => 0.0f
            };
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

        return instructions.ToArray();
    }   
}

[Flags]
internal enum InterpreterOptions
{
    Parallelism = 0x1
}

internal static class Interpreter
{
    public static float[] Evaluate(Instruction[] instructions, int imageSize, InterpreterOptions options = default, float[]? result = null)
    {
        result ??= new float[imageSize * imageSize];

        ParallelOptions parallelOptions = new ParallelOptions()
        {
            MaxDegreeOfParallelism = (options & InterpreterOptions.Parallelism) != 0 ? -1 : 1
        };

        int chunkSize = Vector<float>.Count;
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

            Vector<float> results = Evaluate(instructions, new Vector<float>(xs), new Vector<float>(ys));
            for (int idx = 0; idx < chunkSize; ++idx)
            {
                int currentIdx = chunkIdx * chunkSize + idx;
                (int x, int y) = Parsing.IndexToCoord(currentIdx, width: imageSize);
                result[Parsing.CoordToIndex(x, y, width: imageSize)] = results[idx];
            }
        });

        return result;
    }

    [SkipLocalsInit]
    public static Vector<float> Evaluate(Instruction[] instructions, Vector<float> xs, Vector<float> ys)
    {
        // #TODO: this construction is just a little bit unhinged lol
        Span<Vector<float>> variables = stackalloc Vector<float>[instructions.Length];

        foreach (ref Instruction instruction in instructions.AsSpan())
        {
            variables[instruction.Out] = instruction switch
            {
                { OpCode: OpCode.VarX } => xs,
                { OpCode: OpCode.VarY } => ys,
                
                { OpCode: OpCode.Add, A: { IsConstant: false } a, B: { IsConstant: false } b } => variables[a] + variables[b],
                { OpCode: OpCode.Add, A.IsConstant: true, B: { IsConstant: false } b } => new Vector<float>(instruction.C) + variables[b],
                { OpCode: OpCode.Add, A: { IsConstant: false } a, B.IsConstant: true } => variables[a] + new Vector<float>(instruction.C),
                
                { OpCode: OpCode.Sub, A: { IsConstant: false } a, B: { IsConstant: false } b } => variables[a] - variables[b],
                { OpCode: OpCode.Sub, A.IsConstant: true, B: { IsConstant: false } b } => new Vector<float>(instruction.C) - variables[b],
                { OpCode: OpCode.Sub, A: { IsConstant: false } a, B.IsConstant: true } => variables[a] - new Vector<float>(instruction.C),
                
                { OpCode: OpCode.Mul, A: { IsConstant: false } a, B: { IsConstant: false } b } => variables[a] * variables[b],
                { OpCode: OpCode.Mul, A.IsConstant: true, B: { IsConstant: false } b } => new Vector<float>(instruction.C) * variables[b],
                { OpCode: OpCode.Mul, A: { IsConstant: false } a, B.IsConstant: true } => variables[a] * new Vector<float>(instruction.C),
                
                { OpCode: OpCode.Max, A: var a, B: var b } => Vector.Max(variables[a], variables[b]),
                { OpCode: OpCode.Min, A: var a, B: var b } => Vector.Min(variables[a], variables[b]),
                { OpCode: OpCode.Neg, A: var a } => -variables[a],
                { OpCode: OpCode.Sqrt, A: var a } => Vector.SquareRoot(variables[a]),
                { OpCode: OpCode.Square, A: var a } => variables[a] * variables[a],
                
                { OpCode: OpCode.Const, C: var v } => Vector<float>.One * v,
                _ => variables[instruction.Out]
            };
        }

        return variables[instructions.Length - 1];
    }
}