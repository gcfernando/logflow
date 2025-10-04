using BenchmarkDotNet.Running;
using LogFlow.BenchMark;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
    .Run(args, new Config());
