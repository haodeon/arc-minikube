module Program

open System
open System.Collections.Generic
open System.Security.Cryptography
open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative.Authorization
open Pulumi.AzureNative.KeyVault
open Pulumi.AzureNative.KeyVault.Inputs
open Pulumi.AzureNative.Kubernetes
open Pulumi.AzureNative.Kubernetes.Inputs
open Pulumi.AzureNative.KubernetesConfiguration
open Pulumi.AzureNative.ManagedIdentity
open Pulumi.AzureNative.Resources
open Pulumi.AzureNative.Storage
open Pulumi.AzureNative.Storage.Inputs
open Pulumi.Kubernetes.Types.Inputs.Helm.V3
open Pulumi.Kubernetes.Types.Inputs.Core.V1
open Pulumi.Kubernetes.Types.Inputs.Apps.V1
open Pulumi.Kubernetes.Types.Inputs.Meta.V1
open Pulumi.Kubernetes.Helm.V3
open Pulumi.Kubernetes.Core.V1
open Pulumi.Kubernetes.Apps.V1
open Pulumi.Command.Local
open Pulumi.Tls

let toInputList list = list |> List.map input |> inputList

let mapT0InputList list =
    list |> List.map Union.FromT0 |> List.map input |> inputList

let infra () =
    // Create an Azure Resource Group
    let resourceGroup = ResourceGroup("arc-k8s")

    let agentCertificate =
        PrivateKey("agent", PrivateKeyArgs(Algorithm = "RSA", RsaBits = 4096))

    let base64Key =
        agentCertificate.PrivateKeyPem.Apply(fun pem ->
            let key = RSA.Create(4096)
            key.ImportFromPem(pem)
            key.ExportRSAPublicKey() |> Convert.ToBase64String)

    let createArcK8s (publicKey: Output<string>) =
        ConnectedCluster(
            "minikube",
            ConnectedClusterArgs(
                AgentPublicKeyCertificate = publicKey,
                Identity =
                    ConnectedClusterIdentityArgs(Type = AzureNative.Kubernetes.ResourceIdentityType.SystemAssigned),
                ResourceGroupName = resourceGroup.Name,
                Distribution = "minikube",
                Infrastructure = "generic"
            )
        )

    let connectedCluster = createArcK8s base64Key

    let stackConfig = Config()

    // Create an Azure Storage Account, name of account must match minikube extra config
    let storageAccount =
        StorageAccount(
            "azwi",
            StorageAccountArgs(
                AccountName = stackConfig.Require("storageAccountName"),
                ResourceGroupName = resourceGroup.Name,
                Sku = SkuArgs(Name = SkuName.Standard_LRS),
                Kind = Kind.StorageV2,
                AllowBlobPublicAccess = true
            )
        )

    let azureConfig = Config("azure-native")
    let location = azureConfig.Require("location")
    let subscriptionId = azureConfig.Require("subscriptionId")
    let tenantId = azureConfig.Require("tenantId")

    let k8sAgentValues =
        Output
            .All(resourceGroup.Name, connectedCluster.Name, agentCertificate.PrivateKeyPem)
            .Apply(fun t ->
                let globals = Dictionary<string, obj>()
                globals.Add("subscriptionId", subscriptionId)
                globals.Add("kubernetesDistro", "minikube")
                globals.Add("kubernetesInfra", "generic")
                globals.Add("resourceGroupName", t[0])
                globals.Add("resourceName", t[1])
                globals.Add("location", location)
                globals.Add("tenantId", tenantId)
                globals.Add("onboardingPrivateKey", t[2])
                globals.Add("azureEnvironment", "AZUREPUBLICCLOUD")

                let clusterConnect = new Dictionary<string, obj>()
                clusterConnect.Add("enabled", true)

                let systemDefault = new Dictionary<string, obj>()
                systemDefault.Add("spnOnboarding", false)
                systemDefault.Add("clusterconnect-agent", clusterConnect)

                let values = new Dictionary<string, obj>()
                values.Add("global", globals)
                values.Add("systemDefaultValues", systemDefault)
                values)

    // The OCI URI is "oci://mcr.microsoft.com/azurearck8s/batch1/stable/azure-arc-k8sagents"
    // but the published artifact has incorrect mediatype and Helm refuses to pull
    let localChartPath =
        System.Environment.GetEnvironmentVariable("HOME")
        + "/.azure/AzureArcCharts/azure-arc-k8sagents"

    let createK8sAgent (path: string) (values: Output<Dictionary<string, obj>>) =
        Release(
            "azure-arc-k8sagents",
            ReleaseArgs(
                Chart = path,
                Name = "azure-arc",
                Namespace = "azure-arc-release",
                CreateNamespace = true,
                Values = values
            )
        )

    let k8sAgent = createK8sAgent localChartPath k8sAgentValues

    let storageContainer =
        BlobContainer(
            "oidc",
            BlobContainerArgs(
                AccountName = storageAccount.Name,
                ResourceGroupName = resourceGroup.Name,
                PublicAccess = PublicAccess.Blob
            )
        )

    let getJwks =
        Command("jwks", CommandArgs(Create = "kubectl get --raw /openid/v1/jwks"))

    let jwksOutput =
        getJwks.Stdout.Apply(fun stdout -> StringAsset(stdout) :> AssetOrArchive)

    let createOidcBlob path (jsonString: Output<AssetOrArchive>) =
        Blob(
            path,
            BlobArgs(
                AccountName = storageAccount.Name,
                ContainerName = storageContainer.Name,
                ResourceGroupName = resourceGroup.Name,
                Source = jsonString
            )
        )

    let jwks = createOidcBlob "openid/v1/jwks" jwksOutput

    let oidcDoc =
        let doc =
            {| issuer = Output.Format($"https://{storageAccount.Name}.blob.core.windows.net/{storageContainer.Name}/")
               jwks_uri =
                Output.Format(
                    $"https://{storageAccount.Name}.blob.core.windows.net/{storageContainer.Name}/{jwks.Name}"
                )
               response_types_supported = [| "id_token" |]
               subject_types_support = [| "public" |]
               id_token_signing_alg_values_supported = [| "RS256" |] |}

        doc
        |> Output.Create
        |> Output.JsonSerialize
        |> Outputs.apply (fun jsonString -> StringAsset(jsonString) :> AssetOrArchive)

    let oidcDoc = createOidcBlob ".well-known/openid-configuration" oidcDoc

    let workloadIdentity =
        let values = Dictionary<string, obj>()
        values.Add("azureTenantID", tenantId)

        Release(
            "workload-identity-webhook",
            ReleaseArgs(
                Chart = "workload-identity-webhook",
                Name = "workload-identity-webhook",
                Namespace = "azure-workload-identity-system",
                CreateNamespace = true,
                Values = values,
                RepositoryOpts = RepositoryOptsArgs(Repo = "https://azure.github.io/azure-workload-identity/charts")
            )
        )

    let createK8sExtension name (extensionType: string) configSettings =
        Extension(
            name,
            ExtensionArgs(
                ClusterName = connectedCluster.Name,
                ClusterResourceName = "connectedClusters",
                ClusterRp = "Microsoft.Kubernetes",
                ResourceGroupName = resourceGroup.Name,
                ExtensionName = name,
                ExtensionType = extensionType,
                Identity =
                    Pulumi.AzureNative.KubernetesConfiguration.Inputs.IdentityArgs(
                        Type = Pulumi.AzureNative.KubernetesConfiguration.ResourceIdentityType.SystemAssigned
                    ),
                ConfigurationSettings = configSettings
            ),
            CustomResourceOptions(DependsOn = toInputList [ k8sAgent; workloadIdentity ])
        )

    let secretsProvider =
        let configSettings = InputMap<string>()
        createK8sExtension "akvsecretsprovider" "Microsoft.AzureKeyVaultSecretsProvider" configSettings

    let fluxIdentity =
        UserAssignedIdentity("flux", UserAssignedIdentityArgs(ResourceGroupName = resourceGroup.Name))

    let flux =
        let configSettings =
            fluxIdentity.ClientId.Apply(fun clientId ->
                let config =
                    dict
                        [ ("workloadIdentity.enable", "true")
                          ("workloadIdentity.azureClientId", clientId) ]

                config)
            |> InputMap.op_Implicit

        createK8sExtension "flux" "Microsoft.Flux" configSettings

    let sourceController =
        let issuer =
            Output.Format($"https://{storageAccount.Name}.blob.core.windows.net/{storageContainer.Name}/")

        FederatedIdentityCredential(
            "source-controller",
            FederatedIdentityCredentialArgs(
                ResourceGroupName = resourceGroup.Name,
                ResourceName = fluxIdentity.Name,
                Issuer = issuer,
                Subject = "system:serviceaccount:flux-system:source-controller",
                Audiences = toInputList [ "api://AzureADTokenExchange" ]
            )
        )

    let patchSourceServiceAccount =
        let patch =
            fluxIdentity.ClientId.Apply(fun clientId ->
                let annotations =
                    dict
                        [ ("azure.workload.identity/client-id", clientId)
                          ("azure.workload.identity/tenant-id", tenantId) ]

                annotations)

        ServiceAccountPatch(
            "annotate-client-id",
            ServiceAccountPatchArgs(
                Metadata =
                    ObjectMetaPatchArgs(Annotations = patch, Name = "source-controller", Namespace = "flux-system")
            ),
            CustomResourceOptions(DependsOn = toInputList [ flux ])
        )

    let patchSourceDeployment =
        let label = InputMap<string>()
        label.Add("azure.workload.identity/use", "true")

        DeploymentPatch(
            "label-use-identity",
            DeploymentPatchArgs(
                Metadata = ObjectMetaPatchArgs(Name = "source-controller", Namespace = "flux-system"),
                Spec =
                    DeploymentSpecPatchArgs(
                        Template = PodTemplateSpecPatchArgs(Metadata = ObjectMetaPatchArgs(Labels = label))
                    )
            ),
            CustomResourceOptions(DependsOn = toInputList [ flux ])
        )

    let fluxBlobReader =
        RoleAssignment(
            "flux-blob-reader",
            RoleAssignmentArgs(
                PrincipalId = fluxIdentity.PrincipalId,
                PrincipalType = "ServicePrincipal",
                RoleDefinitionId =
                    "/providers/Microsoft.Authorization/roleDefinitions/2a2b9908-6ea1-4ae2-8e65-a410df84e7d1",
                Scope = storageAccount.Id
            )
        )

    let deployObjectId =
        AzureNative.Authorization.GetClientConfig
            .Invoke()
            .Apply(fun result -> result.ObjectId)

    let clusterKeyVault =
        let accessPolicies =
            [ AccessPolicyEntryArgs(
                  ObjectId = deployObjectId,
                  TenantId = tenantId,
                  Permissions = PermissionsArgs(Secrets = mapT0InputList [ "set"; "delete"; "purge" ])
              )
              AccessPolicyEntryArgs(
                  ObjectId = fluxIdentity.PrincipalId,
                  TenantId = tenantId,
                  Permissions = PermissionsArgs(Secrets = mapT0InputList [ "get" ])
              ) ]
            |> toInputList

        Vault(
            "arck8s",
            VaultArgs(
                ResourceGroupName = resourceGroup.Name,
                VaultName = stackConfig.Require("keyVaultName"),
                Properties =
                    VaultPropertiesArgs(
                        Sku = AzureNative.KeyVault.Inputs.SkuArgs(Family = SkuFamily.A, Name = SkuName.Standard),
                        TenantId = tenantId,
                        EnableSoftDelete = false,
                        AccessPolicies = accessPolicies
                    )
            )
        )

    // Get the primary key
    let primaryKey =
        ListStorageAccountKeysInvokeArgs(ResourceGroupName = resourceGroup.Name, AccountName = storageAccount.Name)
        |> ListStorageAccountKeys.Invoke
        |> Outputs.bind (fun storageKeys -> Output.CreateSecret(storageKeys.Keys[0].Value))

    // Export the primary key for the storage account
    dict
        [ ("connectionString", primaryKey :> obj)
          ("clusterName", connectedCluster.Name)
          ("fluxClientId", fluxIdentity.ClientId)
          ("fluxIdentityName", fluxIdentity.Name)
          ("keyVaultName", clusterKeyVault.Name)
          ("resourceGroupName", resourceGroup.Name)
          ("storageAccountName", storageAccount.Name) ]

[<EntryPoint>]
let main _ = Deployment.run infra
