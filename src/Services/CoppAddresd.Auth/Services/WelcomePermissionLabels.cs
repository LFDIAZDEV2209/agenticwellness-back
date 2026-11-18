namespace CoppAddresd.Auth.Services;

/// <summary>Etiquetas editoriales; el catálogo y el evaluador determinan el acceso, nunca esta traducción.</summary>
public static class WelcomePermissionLabels
{
    private static readonly Dictionary<string, string> Modules = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["Users"] = "Usuarios",
        ["Roles"] = "Roles",
        ["Permissions"] = "Permisos",
        ["Patients"] = "Pacientes",
        ["Employees"] = "Empleados",
        ["Professionals"] = "Profesionales",
        ["Organizations"] = "Organizaciones",
        ["Clinics"] = "Clínicas",
        ["Locations"] = "Sedes",
        ["Appointments"] = "Citas",
        ["Telemedicine"] = "Telemedicina",
        ["ClinicalRecords"] = "Historia clínica",
        ["Documents"] = "Documentos",
        ["Finance"] = "Finanzas",
        ["Reports"] = "Reportes",
        ["Audit"] = "Auditoría",
        ["Inventory"] = "Inventario",
        ["Store"] = "Tienda",
        ["Media"] = "Contenido multimedia",
        ["Agents"] = "Agentes de IA",
        ["System"] = "Sistema",
        ["HealthTests"] = "Evaluaciones de salud",
        ["ProgramProgress"] = "Progreso del programa",
        ["Community"] = "Comunidad",
        ["SOS"] = "SOS",
    };
    private static readonly Dictionary<string, string> Actions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["View"] = "Consultar",
        ["Create"] = "Crear",
        ["Update"] = "Editar",
        ["Delete"] = "Eliminar",
        ["Manage"] = "Gestionar",
        ["Assign"] = "Asignar",
        ["Execute"] = "Ejecutar",
        ["Export"] = "Exportar",
        ["AdminView"] = "Consultar como administrador",
        ["AdminSettings"] = "Configurar el sistema",
    };

    public static string Module(string module, string? lang) =>
        lang?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true
            ? module
            : Modules.GetValueOrDefault(module, module);

    public static string Action(string code, string fallback, string? lang)
    {
        var action = code.Split('.').Last();
        return lang?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true
            ? action
            : Actions.GetValueOrDefault(action, fallback);
    }
}
