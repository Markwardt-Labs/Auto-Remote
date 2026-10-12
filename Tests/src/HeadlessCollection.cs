namespace Markwardt.AutoRemote.Tests;

[CollectionDefinition("Headless", DisableParallelization = true)]
public sealed class HeadlessCollection : ICollectionFixture<HeadlessFixture>;
