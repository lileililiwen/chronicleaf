using Xunit;

// Every test in this assembly shares one real PostgreSQL container and asserts exact row counts,
// so the tests run one at a time. Parallelism would make the row-count assertions lie.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
