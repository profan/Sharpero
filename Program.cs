using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SkiaSharp;

int currentImageSize = 1024;
string combinedOutputString = string.Empty;

(bool success, double totalTimeTakenSecondsOutput) = BenchmarkFunction(() =>
{
    (float[] result, double timeTakenSecondsEvaluate) = BenchmarkFunction(() =>
    {
        Instruction[] instructions = Parsing.Parse("Programs/prospero.vm");
        return Interpreter.Evaluate(instructions, imageSize: currentImageSize);
    });

    combinedOutputString += $" - took: {timeTakenSecondsEvaluate} seconds to evaluate the image!" + Environment.NewLine;

    (bool success, double timeTakenSecondsOutput) = BenchmarkFunction(() =>
    {
        CreateOutputImage(currentImageSize, result, "prospero.jpg");
        return true;
    });

    combinedOutputString += $" - took: {timeTakenSecondsOutput} seconds to write out image!" + Environment.NewLine;

    return true;
});

Console.Write($"Sharpero took: {totalTimeTakenSecondsOutput} seconds to evaluate {currentImageSize}x{currentImageSize} image!" + Environment.NewLine + combinedOutputString);

(T, double) BenchmarkFunction<T>(Func<T> benchmarkedFunction)
{
    var sw = Stopwatch.StartNew();
    T benchmarkedResult = benchmarkedFunction();
    return (benchmarkedResult, sw.Elapsed.TotalSeconds);
}

void CreateOutputImage(int imageSize, float[] imageData, string imageOutputPath)
{
    byte[] imageDataBytes = imageData.Select(p => (byte)(p < 0 ? 255 : 0)).ToArray();
    using var image = SKImage.FromPixelCopy(new SKImageInfo(imageSize, imageSize, SKColorType.Gray8), imageDataBytes);
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
    using var stream = File.OpenWrite(imageOutputPath);
    data.SaveTo(stream);
}

internal enum OpCode { VarX, VarY, Const, Add, Sub, Mul, Max, Min, Neg, Square, Sqrt }

internal readonly record struct Instruction(int Out, OpCode OpCode, int A = -1, int B = -1, float V = float.MaxValue);

internal static class Parsing
{

    // 1D <-> 2D coordinate helpers for square grids
    public static (int x, int y) IndexToCoord(int idx, int width) => (idx % width, idx / width);
    public static int CoordToIndex(int x, int y, int width) => x + (y * width);

    // the identifiers are hexadecimal, stripping off the leading _ is enough :)
    public static int ParseIdentifier(string id) => Convert.ToInt32(id[1..], 16);
    
    public static Instruction[] Parse(string filename)
    {
        List<Instruction> instructions = [];
        
        foreach (string line in File.ReadAllLines(filename))
        {
            Instruction? parsedInstruction = line.Split(" ") switch
            {
                [{ } @out, "var-x"] => new Instruction(ParseIdentifier(@out), OpCode.VarX),
                [{ } @out, "var-y"] => new Instruction(ParseIdentifier(@out), OpCode.VarY),
                [{ } @out, "const", {} v] => new Instruction(ParseIdentifier(@out), OpCode.Const, V: float.Parse(v, CultureInfo.InvariantCulture)),
                [{ } @out, "add", { } a, { } b] => new Instruction(ParseIdentifier(@out), OpCode.Add, ParseIdentifier(a), ParseIdentifier(b)),
                [{ } @out, "sub", { } a, { } b] => new Instruction(ParseIdentifier(@out), OpCode.Sub, ParseIdentifier(a), ParseIdentifier(b)),
                [{ } @out, "mul", { } a, { } b] => new Instruction(ParseIdentifier(@out), OpCode.Mul, ParseIdentifier(a), ParseIdentifier(b)),
                [{ } @out, "max", { } a, { } b] => new Instruction(ParseIdentifier(@out), OpCode.Max, ParseIdentifier(a), ParseIdentifier(b)),
                [{ } @out, "min", { } a, { } b] => new Instruction(ParseIdentifier(@out), OpCode.Min, ParseIdentifier(a), ParseIdentifier(b)),
                [{ } @out, "neg", { } a] => new Instruction(ParseIdentifier(@out), OpCode.Neg, ParseIdentifier(a)),
                [{ } @out, "square", { } a] => new Instruction(ParseIdentifier(@out), OpCode.Square, ParseIdentifier(a)),
                [{ } @out, "sqrt", { } a] => new Instruction(ParseIdentifier(@out), OpCode.Sqrt, ParseIdentifier(a)),
                _ => null
            };

            if (parsedInstruction is { } instruction)
            {
                instructions.Add(instruction);
            }
        }

        return instructions.ToArray();
    }   
}

internal static class Compiler
{
    static (AssemblyBuilder, MethodBuilder, TypeBuilder) CreateDynamicAssemblyWithMethodBuilder(string assemblyName,
        string moduleName, string typeName, string methodName)
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(moduleName);
        var typeBuilder = moduleBuilder.DefineType(typeName, TypeAttributes.Public);
        var methodBuilder =
            typeBuilder.DefineMethod(
                methodName,
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(float),
                [typeof(float), typeof(float)]);
        
        // #TODO: how do this?
        // assemblyBuilder.EntryPoint = methodBuilder;
        
        return (assemblyBuilder, methodBuilder, typeBuilder);
    }

    static void BuildProgramToAssembly(Instruction[] instructions)
    {
        var (assemblyBuilder, methodBuilder, typeBuilder) = CreateDynamicAssemblyWithMethodBuilder(
            assemblyName: "SharperoAssembly",
            moduleName: "SharperoModule",
            typeName: "SharperoMain",
            methodName: "Evaluate");
    }

    static void ExecuteProgramInAssembly()
    {
    }
    
    public static float[] Evaluate(Instruction[] instructions, int imageSize)
    {
        return Array.Empty<float>();
    }
}

internal static class Interpreter
{
    public static float[] Evaluate(Instruction[] instructions, int imageSize)
    {
        float[] result = new float[imageSize * imageSize];

        int chunkSize = Vector<float>.Count;
        Parallel.For(0, (imageSize * imageSize) / chunkSize, chunkIdx =>
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
                { OpCode: OpCode.Add, A: var a, B: var b } => variables[a] + variables[b],
                { OpCode: OpCode.Sub, A: var a, B: var b } => variables[a] - variables[b],
                { OpCode: OpCode.Mul, A: var a, B: var b } => variables[a] * variables[b],
                { OpCode: OpCode.Max, A: var a, B: var b } => Vector.Max(variables[a], variables[b]),
                { OpCode: OpCode.Min, A: var a, B: var b } => Vector.Min(variables[a], variables[b]),
                { OpCode: OpCode.Neg, A: var a } => -variables[a],
                { OpCode: OpCode.Sqrt, A: var a } => Vector.SquareRoot(variables[a]),
                { OpCode: OpCode.Square, A: var a } => variables[a] * variables[a],
                { OpCode: OpCode.Const, V: var v } => Vector<float>.One * v,
                _ => variables[instruction.Out]
            };
        }

        return variables[instructions.Length - 1];
    }
}