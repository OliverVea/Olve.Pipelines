using Olve.Pipelines.Pipelines.Sync;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

public class ManifestCompilerTests
{
    private static readonly ManifestCompiler Compiler = new();

    private static T Pick<T>(Result<T> result)
    {
        result.TryPickProblems(out _, out var value);
        return value!;
    }

    [Test]
    public async Task Compile_InlineConfig_Succeeds()
    {
        var files = new Dictionary<string, string>
        {
            ["config.yaml"] = """
                apiVersion: "0.0"
                name: my-pipeline
                description: A test pipeline
                version: "1.2.3"
                secrets:
                  - name: TOKEN
                    description: registry token
                productionSteps:
                  - name: build
                    configuration:
                      image: alpine
                      script: echo build
                      environmentVariables:
                        REGISTRY_TOKEN: $SECRET:TOKEN
                processingSteps:
                  - name: deploy
                    configuration:
                      image: alpine
                      script: echo deploy
                triggers:
                  - name: redeploy
                    target:
                      type: processing
                      processingStepName: deploy
                """,
        };

        var manifest = Pick(Compiler.Compile(files));

        await Assert.That(manifest.Name).IsEqualTo("my-pipeline");
        await Assert.That(manifest.Description).IsEqualTo("A test pipeline");
        await Assert.That(manifest.Version).IsEqualTo("1.2.3");
        await Assert.That(manifest.Secrets!.Count).IsEqualTo(1);
        await Assert.That(manifest.ProductionSteps!.Count).IsEqualTo(1);
        await Assert.That(manifest.ProductionSteps![0].Configuration!.Script).IsEqualTo("echo build");
        await Assert.That(manifest.ProcessingSteps!.Count).IsEqualTo(1);
        await Assert.That(manifest.Triggers!.Count).IsEqualTo(1);

        var document = manifest.ToDocument();
        await Assert.That(document.ProductionSteps.Count).IsEqualTo(1);
        await Assert.That(document.Triggers[0].Target).IsTypeOf<ProcessingTargetDocument>();
    }

    [Test]
    public async Task Compile_ResolvesStepRefAndScriptFile()
    {
        var files = new Dictionary<string, string>
        {
            ["config.yaml"] = """
                apiVersion: "0.0"
                name: refs
                productionSteps:
                  - $ref: steps/build.yaml
                processingSteps: []
                triggers: []
                """,
            ["steps/build.yaml"] = """
                name: build
                configuration:
                  image: alpine
                  scriptFile: scripts/build.sh
                """,
            ["scripts/build.sh"] = "#!/bin/sh\necho hello from file\n",
        };

        var manifest = Pick(Compiler.Compile(files));

        await Assert.That(manifest.ProductionSteps!.Count).IsEqualTo(1);
        await Assert.That(manifest.ProductionSteps![0].Name).IsEqualTo("build");
        await Assert.That(manifest.ProductionSteps![0].Configuration!.Script).IsEqualTo("#!/bin/sh\necho hello from file\n");
    }

    [Test]
    public async Task Compile_PollTriggerWithSecretHeader_Succeeds()
    {
        var files = new Dictionary<string, string>
        {
            ["config.yaml"] = """
                apiVersion: "0.0"
                name: poller
                secrets:
                  - name: GH
                productionSteps:
                  - name: build
                    configuration:
                      image: alpine
                      script: echo build
                triggers:
                  - name: upstream
                    target:
                      type: poll
                      url: https://example.com/v
                      valuePath: data.sha
                      intervalSeconds: 30
                      headers:
                        Authorization: $SECRET:GH
                """,
        };

        var manifest = Pick(Compiler.Compile(files));
        var target = manifest.Triggers![0].Target as PollTargetDocument;

        await Assert.That(target).IsNotNull();
        await Assert.That(target!.IntervalSeconds).IsEqualTo(30);
        await Assert.That(target.ValuePath).IsEqualTo("data.sha");
    }

    [Test]
    public async Task Compile_MissingConfigFile_Fails()
    {
        var result = Compiler.Compile(new Dictionary<string, string>());
        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Compile_IncompatibleApiVersion_Fails()
    {
        var files = Single("apiVersion: \"1.0\"\nname: x\n");
        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    [Test]
    public async Task Compile_ScriptAndScriptFileBothSet_Fails()
    {
        var files = new Dictionary<string, string>
        {
            ["config.yaml"] = """
                apiVersion: "0.0"
                name: x
                productionSteps:
                  - name: build
                    configuration:
                      image: alpine
                      script: echo inline
                      scriptFile: scripts/build.sh
                """,
            ["scripts/build.sh"] = "echo file",
        };

        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    [Test]
    public async Task Compile_UndeclaredSecret_Fails()
    {
        var files = Single("""
            apiVersion: "0.0"
            name: x
            productionSteps:
              - name: build
                configuration:
                  image: alpine
                  script: echo build
                  environmentVariables:
                    T: $SECRET:MISSING
            """);

        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    [Test]
    public async Task Compile_TriggerReferencesUnknownStep_Fails()
    {
        var files = Single("""
            apiVersion: "0.0"
            name: x
            processingSteps:
              - name: deploy
                configuration:
                  image: alpine
                  script: echo deploy
            triggers:
              - name: t
                target:
                  type: processing
                  processingStepName: ghost
            """);

        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    [Test]
    public async Task Compile_DuplicateStepNames_Fails()
    {
        var files = Single("""
            apiVersion: "0.0"
            name: x
            productionSteps:
              - name: build
                configuration:
                  image: alpine
                  script: a
              - name: build
                configuration:
                  image: alpine
                  script: b
            """);

        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    [Test]
    public async Task Compile_MissingScriptFile_Fails()
    {
        var files = Single("""
            apiVersion: "0.0"
            name: x
            productionSteps:
              - name: build
                configuration:
                  image: alpine
                  scriptFile: scripts/nope.sh
            """);

        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    [Test]
    public async Task Compile_FailureHandler_Valid_Succeeds()
    {
        var files = Single("""
            apiVersion: "0.0"
            name: x
            productionSteps:
              - name: build
                configuration:
                  image: alpine
                  script: echo build
            failureHandlers:
              - handler: aoe-triage
                steps: [build]
                env:
                  AOE_BASE_URL: http://aoe.aoe.svc.cluster.local
            """);

        var manifest = Pick(Compiler.Compile(files));

        await Assert.That(manifest.FailureHandlers!.Count).IsEqualTo(1);
        await Assert.That(manifest.FailureHandlers![0].Handler).IsEqualTo("aoe-triage");
        await Assert.That(manifest.FailureHandlers![0].Steps!).Contains("build");
        await Assert.That(manifest.FailureHandlers![0].Env!["AOE_BASE_URL"]).IsEqualTo("http://aoe.aoe.svc.cluster.local");
    }

    [Test]
    public async Task Compile_FailureHandler_UnknownHandlerName_Fails()
    {
        var files = Single("""
            apiVersion: "0.0"
            name: x
            failureHandlers:
              - handler: not-a-real-handler
            """);

        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    [Test]
    public async Task Compile_FailureHandler_UnknownStep_Fails()
    {
        var files = Single("""
            apiVersion: "0.0"
            name: x
            failureHandlers:
              - handler: aoe-triage
                steps: [ghost]
            """);

        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    [Test]
    public async Task Compile_FailureHandler_UndeclaredSecretInEnv_Fails()
    {
        var files = Single("""
            apiVersion: "0.0"
            name: x
            failureHandlers:
              - handler: aoe-triage
                env:
                  AOE_TOKEN: $SECRET:MISSING
            """);

        await Assert.That(Compiler.Compile(files).Failed).IsTrue();
    }

    private static Dictionary<string, string> Single(string configYaml) => new() { ["config.yaml"] = configYaml };
}
