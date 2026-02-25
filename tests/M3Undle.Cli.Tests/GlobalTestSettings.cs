using Microsoft.VisualStudio.TestTools.UnitTesting;

// Configure test parallelization behavior for the entire test assembly
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]
