using Scada.Acquisition;
using Scada.Acquisition.Alarms;
using Scada.Acquisition.Commands;
using Scada.Acquisition.Devices;
using Scada.Acquisition.Engine;
using Scada.Acquisition.Historian;
using Scada.Core.Data;
using Scada.Drivers;
using Scada.Drivers.Modbus;
using Scada.Drivers.Simulator;

var builder = Host.CreateApplicationBuilder(args);

// systemd: Type=notify, журнал в journald с уровнями
builder.Services.AddSystemd();
builder.Configuration.AddEnvironmentVariables("SCADA_");

builder.Services.Configure<AcquisitionOptions>(builder.Configuration.GetSection("Acquisition"));

var connectionString = builder.Configuration.GetConnectionString("Scada")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Scada");
builder.Services.AddSingleton(_ => Db.CreateDataSource(connectionString, "scada-acquisition"));

// драйверы протоколов
builder.Services.AddSingleton<IDriverProvider, SimulatorDriverProvider>();
builder.Services.AddSingleton<IDriverProvider, ModbusTcpDriverProvider>();
#if OPCUA
builder.Services.AddSingleton<IDriverProvider, Scada.Drivers.OpcUa.OpcUaDriverProvider>();
#endif
builder.Services.AddSingleton<DriverRegistry>();

builder.Services.AddSingleton<HistorianWriter>();
builder.Services.AddSingleton<AlarmService>();
builder.Services.AddSingleton<TagEngine>();
builder.Services.AddSingleton<ValuePublisher>();
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddHostedService<AcquisitionHost>();

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

await builder.Build().RunAsync();
