namespace CoppAddresd.Api.Constants;

/// <summary>
/// Códigos de permiso que la API principal protege. Deben coincidir con los
/// seedeados en el Auth Service (mismo catálogo central).
/// </summary>
public static class PermissionCodes
{
    public const string MedicationsView = "Medications.View";
    public const string MedicationsManage = "Medications.Manage";
    public const string MedicationsManageAccess = "Medications.ManageAccess";
    public const string OrganizationsView = "Organizations.View";
    public const string OrganizationsCreate = "Organizations.Create";
    public const string OrganizationsUpdate = "Organizations.Update";
    public const string OrganizationsDelete = "Organizations.Delete";

    public const string ClinicsView = "Clinics.View";
    public const string ClinicsCreate = "Clinics.Create";
    public const string ClinicsUpdate = "Clinics.Update";
    public const string ClinicsDelete = "Clinics.Delete";

    public const string LocationsView = "Locations.View";
    public const string LocationsCreate = "Locations.Create";
    public const string LocationsUpdate = "Locations.Update";
    public const string LocationsDelete = "Locations.Delete";

    public const string EmployeesView = "Employees.View";
    public const string EmployeesCreate = "Employees.Create";
    public const string EmployeesUpdate = "Employees.Update";
    public const string EmployeesDelete = "Employees.Delete";

    public const string ProfessionalsView = "Professionals.View";
    public const string ProfessionalsCreate = "Professionals.Create";
    public const string ProfessionalsUpdate = "Professionals.Update";
    public const string ProfessionalsDelete = "Professionals.Delete";

    public const string PatientsView = "Patients.View";
    public const string PatientsCreate = "Patients.Create";
    public const string PatientsUpdate = "Patients.Update";
    public const string PatientsDelete = "Patients.Delete";

    // Código existente en Auth; asignar una instancia modifica el acceso al agente.
    public const string AgentsUpdate = "Agents.Update";

    // Progreso del Programa (T-04): códigos sembrados en el Auth Service.
    public const string ProgramView = "Program.View";
    public const string ProgramEdit = "Program.Edit";
    public const string ProgramEnroll = "Program.Enroll";
    public const string ProgramAdapt = "Program.Adapt";
    public const string ProgramForceComplete = "Program.ForceComplete";
    public const string ProgramExport = "Program.Export";

    // Tests de Salud (módulo HealthTests).
    public const string HealthTestsView = "HealthTests.View";
    public const string HealthTestsViewOwn = "HealthTests.ViewOwn";
    public const string HealthTestsManage = "HealthTests.Manage";
    public const string HealthTestsAssign = "HealthTests.Assign";
    public const string HealthTestsReview = "HealthTests.Review";
    public const string HealthTestsNotify = "HealthTests.Notify";

    // Documentos Legales
    public const string LegalDocumentsView = "LegalDocuments.View";
    public const string LegalDocumentsManage = "LegalDocuments.Manage";
    public const string LegalAcceptancesView = "LegalAcceptances.View";
    public const string LegalAcceptancesExport = "LegalAcceptances.Export";

    // Store
    public const string StoreView = "Store.View";
    public const string StoreManage = "Store.Manage";

    // Medios multimedia (biblioteca de podcasts del ERP, change erp-program-content-admin).
    public const string MediaView = "Media.View";
    public const string MediaCreate = "Media.Create";
    public const string MediaEdit = "Media.Edit";
    public const string MediaPublish = "Media.Publish";
    public const string MediaArchive = "Media.Archive";
    public const string MediaDelete = "Media.Delete";

    // Wellness
    public const string WellnessView = "Wellness.View";
    public const string WellnessManage = "Wellness.Manage";
    public const string NutritionPlansApprove = "NutritionPlans.Approve";
    // Capacidades de Bienestar separadas de dashboards y de las escrituras.
    public const string ProgramProfile360View = "Program.Profile360View";
    public const string ProgramAiView = "Program.AiView";
    public const string ProgramAiManage = "Program.AiManage";
    public const string NutritionView = "Nutrition.View";
    public const string NutritionRecordsView = "Nutrition.RecordsView";
    public const string NutritionPhotosView = "Nutrition.PhotosView";
    public const string NutritionNotesView = "Nutrition.NotesView";
    public const string NutritionNotesManage = "Nutrition.NotesManage";
    public const string NutritionReviewManage = "Nutrition.ReviewManage";
    public const string NutritionManage = "Nutrition.Manage";
    public const string NutritionApprove = "Nutrition.Approve";

    // Mantenimiento y administración del sistema.
    public const string AuditView = "Audit.View";
    public const string SystemAdminSettings = "System.AdminSettings";

    // SOS real (change sos-panic-real): atención de alertas por staff ERP.
    public const string SosAlertsManage = "Sos.Alerts.Manage";

    /// <summary>Nombres conocidos (para el policy provider que resuelve políticas por código).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        MedicationsView,
        MedicationsManage,
        MedicationsManageAccess,
        OrganizationsView,
        OrganizationsCreate,
        OrganizationsUpdate,
        OrganizationsDelete,
        ClinicsView,
        ClinicsCreate,
        ClinicsUpdate,
        ClinicsDelete,
        LocationsView,
        LocationsCreate,
        LocationsUpdate,
        LocationsDelete,
        EmployeesView,
        EmployeesCreate,
        EmployeesUpdate,
        EmployeesDelete,
        ProfessionalsView,
        ProfessionalsCreate,
        ProfessionalsUpdate,
        ProfessionalsDelete,
        PatientsView,
        PatientsCreate,
        PatientsUpdate,
        PatientsDelete,
        AgentsUpdate,
        ProgramView,
        ProgramEdit,
        ProgramEnroll,
        ProgramAdapt,
        ProgramForceComplete,
        HealthTestsView,
        HealthTestsViewOwn,
        HealthTestsManage,
        HealthTestsAssign,
        HealthTestsReview,
        HealthTestsNotify,
        LegalDocumentsView,
        LegalDocumentsManage,
        LegalAcceptancesView,
        LegalAcceptancesExport,
        StoreView,
        StoreManage,
        MediaView,
        MediaCreate,
        MediaEdit,
        MediaPublish,
        MediaArchive,
        MediaDelete,
        WellnessView,
        WellnessManage,
        NutritionPlansApprove,
        ProgramProfile360View,
        ProgramAiView,
        ProgramAiManage,
        NutritionView,
        NutritionRecordsView,
        NutritionPhotosView,
        NutritionNotesView,
        NutritionNotesManage,
        NutritionReviewManage,
        NutritionManage,
        NutritionApprove,
        AuditView,
        SystemAdminSettings,
        SosAlertsManage,
    ];
}
