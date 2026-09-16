using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Cli.Commands;
using Orchestrator.Infrastructure.Identity;
using Orchestrator.Infrastructure.Persistence;

var loginOption = new Option<string>("--login", "Login name for the new admin account (stored lowercase).")
{
    IsRequired = true,
};

var provisionAdmin = new Command("provision-admin", "Provision a new admin account. Reads the password from stdin.")
{
    loginOption,
};

provisionAdmin.SetHandler(async (InvocationContext context) =>
{
    var login = context.ParseResult.GetValueForOption(loginOption)!;
    var ct = context.GetCancellationToken();

    // The password is never accepted as a command-line argument: argv is visible to
    // other users via the process table and lands in shell history. It is read from
    // stdin instead, with the prompt written to stderr so stdout stays clean for
    // scripting and the prompt doesn't get piped into the password itself.
    await Console.Error.WriteLineAsync("Password (piped via stdin):");
    var password = await Console.In.ReadLineAsync() ?? string.Empty;

    var connectionString = Environment.GetEnvironmentVariable("ORCHESTRATOR_CONNECTION_STRING")
        ?? throw new InvalidOperationException(
            "Orchestrator database connection string is not configured. Set ORCHESTRATOR_CONNECTION_STRING.");

    await using var db = new OrchestratorDbContext(
        new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options);

    var command = new ProvisionAdminCommand(db, new Pbkdf2PasswordHasher(), TimeProvider.System);
    await command.ExecuteAsync(login, password, ct);

    await Console.Error.WriteLineAsync($"Admin account '{login.Trim().ToLowerInvariant()}' provisioned.");
});

var root = new RootCommand("Orchestrator administrative CLI") { provisionAdmin };

return await root.InvokeAsync(args);
