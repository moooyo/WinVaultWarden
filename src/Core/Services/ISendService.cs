namespace Core.Services;

public interface ISendService
{
    Task<IReadOnlyList<Core.Models.Send>> GetSendsAsync(CancellationToken ct = default);

    Task<Core.Models.SendReadSnapshot> RefreshSendsAsync(CancellationToken ct = default) =>
        ObserveSendsAsync(ct);

    async Task<Core.Models.SendReadSnapshot> ObserveSendsAsync(CancellationToken ct = default)
    {
        var sends = await GetSendsAsync(ct);
        return new Core.Models.SendReadSnapshot(
            sends.Select(send => new Core.Models.SendObservation(
                send.Id,
                send.RevisionDate,
                Core.Models.SendDecryptStatus.Decrypted,
                send,
                send.RawMutationWitness)).ToArray());
    }
}
