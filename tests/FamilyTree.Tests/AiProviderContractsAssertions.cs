using System.Runtime.CompilerServices;
using FamilyTree.Domain;
internal static class AiProviderContractsAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var provider = new CountingProvider();
        var input = new SyntheticSuggestionInput("fictional only", "Suggest a fictional story");
        Check(SyntheticProviderGate.TrySuggestAsync(provider,input,false,true).GetAwaiter()
                .GetResult() is null && provider.Calls == 0, "consent gate");
        Check(SyntheticProviderGate.TrySuggestAsync(provider,input,true,false).GetAwaiter()
                .GetResult() is null && provider.Calls == 0, "permission gate");
        Check(SyntheticProviderGate.TrySuggestAsync(provider,input,true,true).GetAwaiter()
                .GetResult()?.DraftText == "pending fictional suggestion" &&
                provider.Calls == 1, "provider-neutral allowed draft");
        try {
            new DisabledGenealogySuggestionProvider().SuggestSyntheticAsync(input)
                .GetAwaiter().GetResult();
            throw new Exception("default disabled provider unexpectedly called");
        } catch (InvalidOperationException) { }
        Console.WriteLine("PASS provider-neutral opt-in safety checks");
    }
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private sealed class CountingProvider : IGenealogySuggestionProvider
    {
        public int Calls { get; private set; }
        public Task<GeneratedSuggestion> SuggestSyntheticAsync(SyntheticSuggestionInput input,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new GeneratedSuggestion("pending fictional suggestion", "test"));
        }
    }
}