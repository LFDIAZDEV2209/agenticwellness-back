using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.IntegrationTests;

/// <summary>Regresión de identidad de medicamentos legacy con PostgreSQL real y pacientes de prueba aislados.</summary>
public sealed class PatientMedicationIdentityTests : IAsyncLifetime
{
    private readonly string? _connection = Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION");
    private readonly Guid _patientId = Guid.NewGuid();
    private readonly Guid[] _catalogIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    private readonly Guid[] _legacyIds = [Guid.NewGuid(), Guid.NewGuid()];
    private readonly DateTime _created = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connection)) return;
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connection).Options);
        _db.Medications.AddRange(_catalogIds.Select((id, index) => new Medication { Id = id, Name = $"QA medicamento {index}" }));
        _db.PatientProfiles.Add(new PatientProfile { Id = _patientId, FirstName = "QA", LastName = "Identidad de medicamentos" });
        _db.PatientMedications.AddRange(_legacyIds.Select((id, index) => new PatientMedication
        {
            Id = id, PatientId = _patientId, MedicationId = _catalogIds[index], Frequency = "Cada día",
            SortOrder = index, CreatedAt = _created,
        }));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_db is null) return;
        await _db.PatientMedications.Where(x => x.PatientId == _patientId).ExecuteDeleteAsync();
        await _db.PatientProfiles.Where(x => x.Id == _patientId).ExecuteDeleteAsync();
        await _db.Medications.Where(x => _catalogIds.Contains(x.Id)).ExecuteDeleteAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task AggregateUpdate_IdenticalMedicationList_PreservesIdsAndCreationTimes()
    {
        if (string.IsNullOrWhiteSpace(_connection)) return;
        var repository = new PatientRepository(_db);
        var patient = (await repository.GetByIdAsync(_patientId))!;
        // El handler legacy reconstruye los hijos desde medicationId/frequency, sin IDs de fila.
        patient.Medications = patient.Medications.Select(x => new PatientMedication
        {
            Id = Guid.NewGuid(), MedicationId = x.MedicationId, Frequency = x.Frequency, SortOrder = x.SortOrder,
        }).ToList();
        patient.Notes = "Solo cambió una nota";

        await repository.UpdateAsync(patient);

        var rows = await _db.PatientMedications.AsNoTracking().Where(x => x.PatientId == _patientId).OrderBy(x => x.SortOrder).ToListAsync();
        Assert.Equal(_legacyIds, rows.Select(x => x.Id));
        Assert.All(rows, x => Assert.Equal(_created, x.CreatedAt));
        Assert.Equal("Solo cambió una nota", (await repository.GetByIdAsync(_patientId))!.Notes);
    }

    [Fact]
    public async Task AggregateUpdate_ReordersEditsAddsAndRemoves_OnlyRemovedCatalogEntryLosesItsRow()
    {
        if (string.IsNullOrWhiteSpace(_connection)) return;
        var repository = new PatientRepository(_db);
        var patient = (await repository.GetByIdAsync(_patientId))!;
        patient.Medications =
        [
            new PatientMedication { Id = Guid.NewGuid(), MedicationId = _catalogIds[2], Frequency = "Nueva indicación", SortOrder = 0 },
            new PatientMedication { Id = Guid.NewGuid(), MedicationId = _catalogIds[0], Frequency = "Dos veces al día", SortOrder = 1 },
        ];

        await repository.UpdateAsync(patient);

        var rows = await _db.PatientMedications.AsNoTracking().Where(x => x.PatientId == _patientId).OrderBy(x => x.SortOrder).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(_catalogIds[2], rows[0].MedicationId);
        Assert.DoesNotContain(rows[0].Id, _legacyIds);
        Assert.Equal(_legacyIds[0], rows[1].Id);
        Assert.Equal(_created, rows[1].CreatedAt);
        Assert.Equal("Dos veces al día", rows[1].Frequency);
        Assert.Equal(1, rows[1].SortOrder);
        Assert.DoesNotContain(rows, row => row.Id == _legacyIds[1]);
    }
}
