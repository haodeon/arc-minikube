module Program

open System.Collections.Generic
open System.Text.Json
open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative.KeyVault
open Pulumi.AzureNative.KeyVault.Inputs
open Pulumi.AzureNative.KubernetesConfiguration
open Pulumi.AzureNative.ManagedIdentity
open Pulumi.AzureNative.Resources
open Pulumi.AzureNative.Storage
open Pulumi.AzureNative.Storage.Inputs
open Pulumi.Cloudflare
open Pulumi.Random
open Yzl
open Manifest

let infra () =
    let stackConfig = Config()
    let stack = Deployment.Instance.StackName
    let org = Deployment.Instance.OrganizationName

    let stackRef = StackReference($"{org}/arc-k8s/{stack}")

    let getOutputDetail reference convert =
        task {
            let! stackOutput = stackRef.GetOutputDetailsAsync(reference)
            return convert stackOutput.Value
        }

    let objToString (obj: obj) = obj :?> string

    let arcResourceGroupName = getOutputDetail "resourceGroupName" objToString

    let clusterName = getOutputDetail "clusterName" objToString

    let fluxClientId = getOutputDetail "fluxClientId" objToString

    let fluxIdentityName = getOutputDetail "fluxIdentityName" objToString

    let keyVaultName = getOutputDetail "keyVaultName" objToString

    let storageAccountName = getOutputDetail "storageAccountName" objToString

    let azureConfig = Config("azure-native")
    let tenantId = azureConfig.Require("tenantId")

    let storageContainer =
        BlobContainer(
            "cluster-config",
            BlobContainerArgs(
                AccountName = storageAccountName.Result,
                ResourceGroupName = arcResourceGroupName.Result,
                PublicAccess = PublicAccess.None
            )
        )

    let createYamlBlob path yamlString =
        Blob(
            path,
            BlobArgs(
                AccountName = storageAccountName.Result,
                ContainerName = storageContainer.Name,
                ResourceGroupName = arcResourceGroupName.Result,
                Source = yamlString
            )
        )

    // RandomBytes is preferred but tf bridge panics on Check
    // let tunnelSecret = RandomBytes("tunnel-secret", RandomBytesArgs(Length = 32))
    let tunnelSecret = stackConfig.Require("tunnelSecret")
    let tunnelAccount = stackConfig.Require("tunnelAccount")

    let tunnel =
        Tunnel(
            "arc-k8s",
            TunnelArgs(AccountId = tunnelAccount, Name = "arc-k8s", Secret = tunnelSecret),
            CustomResourceOptions(DeleteBeforeReplace = true)
        )

    let tunnelNamespaceYaml =
        Namespace.yaml "cloudflare" |> StringAsset :> AssetOrArchive
        |> Input.op_Implicit

    let tunnelNamespaceBlob =
        createYamlBlob "cloudflare/namespace.yaml" tunnelNamespaceYaml

    let tunnelHelmRepoYaml =
        HelmRepository.cloudflare |> StringAsset :> AssetOrArchive |> Input.op_Implicit

    let tunnelHelmRepoBlob =
        createYamlBlob "cloudflare/helmrepository.yaml" tunnelHelmRepoYaml

    let tunnelHelmReleaseYaml =
        Output
            .All(tunnel.Name, tunnel.Id)
            .Apply(fun t ->
                let valuesInline =
                    [ "cloudflare"
                      .= [ "account" .= tunnelAccount
                           "tunnelName" .= t[0]
                           "tunnelId" .= t[1]
                           "secretName" .= "csi"
                           "ingress"
                           .= [ [ "hostname" .= stackConfig.Require("hostname"); "service" .= "hello_world" ] ] ] ]

                HelmRelease.cloudflareTunnel valuesInline |> StringAsset :> AssetOrArchive)

    let tunnelHelmReleaseBlob =
        createYamlBlob "cloudflare/helmrelease.yaml" (Input.op_Implicit tunnelHelmReleaseYaml)

    let tunnelCredentials =
        let credentials =
            tunnel.Id.Apply(fun id ->
                {| AccountTag = tunnelAccount
                   TunnelID = id
                   TunnelSecret = tunnelSecret |}
                |> JsonSerializer.Serialize)

        Secret(
            "tunnel-secret",
            SecretArgs(
                ResourceGroupName = arcResourceGroupName.Result,
                VaultName = keyVaultName.Result,
                Properties = SecretPropertiesArgs(Value = credentials)
            )
        )

    let tunnelSecretYaml =
        SecretProviderClass.cloudflareTunnel fluxClientId.Result keyVaultName.Result tenantId
        |> StringAsset
        :> AssetOrArchive

    let tunnelSecretBlob =
        createYamlBlob "cloudflare/secretproviderclass.yaml" (Input.op_Implicit tunnelSecretYaml)

    let cloudflareTunnel =
        let issuer = $"https://{storageAccountName.Result}.blob.core.windows.net/oidc/"

        FederatedIdentityCredential(
            "cloudflare-tunnel",
            FederatedIdentityCredentialArgs(
                ResourceGroupName = arcResourceGroupName.Result,
                ResourceName = fluxIdentityName.Result,
                Issuer = issuer,
                Subject = "system:serviceaccount:cloudflare:cloudflare-tunnel",
                Audiences = inputList [ input "api://AzureADTokenExchange" ]
            )
        )

    let tunnelKustomisation =
        Inputs.KustomizationDefinitionArgs(Path = "./cloudflare", Prune = true)

    let clusterKustomisations =
        let kustomizationsMap =
            Dictionary<string, Pulumi.AzureNative.KubernetesConfiguration.Inputs.KustomizationDefinitionArgs>()

        kustomizationsMap.Add("cloudflare", tunnelKustomisation)
        kustomizationsMap

    let clusterConfig =
        FluxConfiguration(
            "cluster-config",
            FluxConfigurationArgs(
                ClusterName = clusterName.Result,
                ClusterResourceName = "connectedClusters",
                ClusterRp = "Microsoft.Kubernetes",
                ResourceGroupName = arcResourceGroupName.Result,
                FluxConfigurationName = "cluster-config",
                Namespace = "cluster-config",
                SourceKind = "AzureBlob",
                AzureBlob =
                    Inputs.AzureBlobDefinitionArgs(
                        Url = Output.Format($"https://{storageAccountName.Result}.blob.core.windows.net/"),
                        ContainerName = storageContainer.Name
                    ),
                Kustomizations = clusterKustomisations
            )
        )

    (*
    // Export the primary key for the storage account
    dict [("connectionString", primaryKey :> obj)]
*)
    dict []

[<EntryPoint>]
let main _ = Deployment.run infra
