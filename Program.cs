// Monitor de citas EMTRA - Traspaso de propiedad vehículo
// Consulta la API pública de agendamiento.emtra.com.co y avisa por correo SOLO cuando hay horas disponibles.
//
// Uso:
//   dotnet run            -> revisa una vez y termina (modo GitHub Actions)
//   dotnet run -- loop    -> revisa cada 10 min sin parar (modo PC)
//
// Variables de entorno (en GitHub van como Secrets):
//   GMAIL_USER          correo Gmail que envía
//   GMAIL_APP_PASSWORD  contraseña de aplicación de Gmail
//   MAIL_TO             correo que recibe el aviso (opcional, por defecto GMAIL_USER)

using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;

const string BaseUrl = "https://agendamiento.emtra.com.co/api/v1/public/";
const string TramiteUuid = "b4b551e3-c2fb-4ec5-8ece-031c21c47e59"; // TRASPASO DE PROPIEDAD VEHICULO
const string ArchivoEstado = "notificados.json";
var intervalo = TimeSpan.FromMinutes(10);

using var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MonitorCitas");


if (Environment.GetEnvironmentVariable("PRUEBA_CORREO") == "true")
{
    await EnviarCorreoAsync("✅ Prueba Monitor EMTRA", "Si ves este correo, los avisos de citas funcionan.");
    Console.WriteLine("Correo de prueba enviado.");
    return;
}

do
{
    try
    {
        await RevisarAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{Ahora():dd/MM HH:mm} - Error: {ex.Message}");
        if (!args.Contains("loop")) Environment.ExitCode = 1;
    }
    if (args.Contains("loop")) await Task.Delay(intervalo);
} while (args.Contains("loop"));

async Task RevisarAsync()
{
    var sedes = await http.GetFromJsonAsync<List<Sede>>($"locations_by_transact_type/{TramiteUuid}/") ?? [];
    var cupos = new List<string>();

    foreach (var sede in sedes)
    {
        var dias = await http.GetFromJsonAsync<List<Dia>>($"days_by_location/{sede.uuid}/") ?? [];
        foreach (var dia in dias)
        {
            if (!DiaPermitido(dia.date)) continue;
            var horas = await http.GetFromJsonAsync<List<string>>(
                $"availability_hours/{sede.uuid}/{TramiteUuid}/{dia.date}/") ?? [];
            foreach (var h in horas)
                cupos.Add($"{sede.name}|{dia.date}|{h}");
            await Task.Delay(300); // no saturar el servidor
        }
    }

    // Solo avisar de cupos que no se hayan notificado antes
    var notificados = File.Exists(ArchivoEstado)
        ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ArchivoEstado)) ?? []
        : [];
    var nuevos = cupos.Where(c => !notificados.Contains(c)).ToList();

    Console.WriteLine($"{Ahora():dd/MM HH:mm} - {cupos.Count} cupos, {nuevos.Count} nuevos");

    if (nuevos.Count > 0)
    {
        var cuerpo = "Hay citas disponibles para TRASPASO DE PROPIEDAD VEHICULO:\n\n" +
            string.Join("\n", nuevos
                .Select(c => c.Split('|'))
                .GroupBy(p => $"{p[0]} - {p[1]}")
                .Select(g => $"• {g.Key}: {string.Join(", ", g.Select(p => p[2]))}")) +
            "\n\nAgenda aquí: https://agendamiento.emtra.com.co/schedule";

        await EnviarCorreoAsync("🚗 Citas EMTRA disponibles", cuerpo);
        Console.WriteLine(cuerpo);
    }

    // Guardar solo los cupos vigentes (los que ya no existen se olvidan, así si vuelven a salir se avisa otra vez)
    File.WriteAllText(ArchivoEstado, JsonSerializer.Serialize(cupos.ToHashSet()));
}

async Task EnviarCorreoAsync(string asunto, string cuerpo)
{
    var user = Environment.GetEnvironmentVariable("GMAIL_USER");
    var pass = Environment.GetEnvironmentVariable("GMAIL_APP_PASSWORD");
    var to = Environment.GetEnvironmentVariable("MAIL_TO");
    if (string.IsNullOrWhiteSpace(to)) to = user;

    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
    {
        Console.WriteLine("GMAIL_USER / GMAIL_APP_PASSWORD no configurados: no se envía correo.");
        return;
    }

    using var smtp = new SmtpClient("smtp.gmail.com", 587)
    {
        EnableSsl = true,
        Credentials = new NetworkCredential(user, pass)
    };
    await smtp.SendMailAsync(new MailMessage(user, to!, asunto, cuerpo));
}

// SOLO_DIAS: días de la semana a vigilar, separados por coma (ej: "sabado" o "viernes,sabado"). Vacío = todos.
static bool DiaPermitido(string fecha)
{
    var filtro = Environment.GetEnvironmentVariable("SOLO_DIAS");
    if (string.IsNullOrWhiteSpace(filtro)) return true;
    var d = DateTime.ParseExact(fecha, "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
    string[] nombres = ["domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado"];
    return filtro.ToLowerInvariant().Replace("á", "a").Replace("é", "e")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Contains(nombres[(int)d.DayOfWeek]);
}

static DateTime Ahora() =>
    TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Bogota");

record Sede(string uuid, string name, string address);
record Dia(int day, string date, string open_hour, string close_hour);
