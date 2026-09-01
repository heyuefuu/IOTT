namespace IndustrialIoT.Application.Commands;

using IndustrialIoT.Domain.Interfaces;
using MediatR;

public record DeleteDeviceCommand(string DeviceId) : IRequest<bool>;

public class DeleteDeviceCommandHandler : IRequestHandler<DeleteDeviceCommand, bool>
{
    private readonly IDeviceRepository _repo;
    public DeleteDeviceCommandHandler(IDeviceRepository repo) => _repo = repo;

    public async Task<bool> Handle(DeleteDeviceCommand command, CancellationToken ct)
    {
        await _repo.DeleteAsync(command.DeviceId, ct);
        return true;
    }
}
