using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaOpennessMcpServer.Openness;

// Compilation is an explicit full-access operation. It neither saves nor changes online state.
internal static class OpennessCompiler
{
    public static CompileResult Run(Project project, CompileRequest request, Action validate)
    {
        var result = new CompileResult { ProcessId = request.ProcessId, PlcObjectId = request.PlcObjectId };
        var read = new DiscoveryReadContext(result.Errors, validate);
        try
        {
            var identifiers = Native(() => project.GetService<ObjectIdentifierProvider>(), validate) ??
                throw new ConnectionFault("unsupportedObject", request.ProcessId, "The project does not expose ObjectIdentifierProvider.");
            var target = Native(() => identifiers.Find(request.PlcObjectId), validate) ??
                throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected CPU DeviceItem was not found.");
            if (target is not DeviceItem deviceItem)
                throw new ConnectionFault("unsupportedObject", request.ProcessId, "plcObjectId must identify a CPU DeviceItem whose SoftwareContainer owns PlcSoftware.");
            var container = Native(() => deviceItem.GetService<SoftwareContainer>(), validate);
            var software = Native(() => container?.Software, validate) as PlcSoftware ??
                throw new ConnectionFault("unsupportedObject", request.ProcessId, "plcObjectId must identify a CPU DeviceItem whose SoftwareContainer owns PlcSoftware.");
            var compilable = Native(() => software.GetService<ICompilable>(), validate) ??
                throw new ConnectionFault("unsupportedObject", request.ProcessId, "The selected PLC software does not expose ICompilable.");
            var native = Native(() => compilable.Compile(), validate);
            if (native == null)
                result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = "compile_plc", Message = "The native compile returned no result object." });
            else
                new CompileResultReader(result, validate).Read(result, new CompileResultNode
                {
                    State = () => native.State.ToString(), ErrorCount = () => native.ErrorCount,
                    WarningCount = () => native.WarningCount, Messages = () => native.Messages.Select(Message)
                });
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex) { read.Failure(ex, "compile_plc", null); }
        validate();
        result.ProjectModified = read.Read(() => Native(() => (bool?)project.IsModified, validate), "projectModified", null);
        validate();
        return result;
    }

    private static T Native<T>(Func<T> read, Action validate)
    {
        validate();
        var value = read();
        validate();
        return value;
    }

    private static CompileMessageNode Message(CompilerResultMessage message) => new()
    {
        Path = () => message.Path, DateTime = () => message.DateTime, State = () => message.State.ToString(),
        Description = () => message.Description, ErrorCount = () => message.ErrorCount,
        WarningCount = () => message.WarningCount, Messages = () => message.Messages.Select(Message)
    };
}
