namespace Manifest

open Yzl

module Namespace =
    let apiVersion = Yzl.str
    let kind = Yzl.str
    let metadata = Yzl.map
    let name = Yzl.str

    let yaml (namespaceName: string) =
        [ apiVersion "v1"; kind "Namespace"; metadata [ name namespaceName ] ]
        |> Yzl.render


module HelmRepository =
    let apiVersion = Yzl.str
    let kind = Yzl.str
    let metadata = Yzl.map
    let name = Yzl.str
    let ``namespace`` = Yzl.str
    let spec = Yzl.map
    let url = Yzl.str
    let interval = Yzl.str

    let cloudflare =
        [ apiVersion "source.toolkit.fluxcd.io/v1beta2"
          kind "HelmRepository"
          metadata [ name "cloudflare"; ``namespace`` "cluster-config" ]
          spec [ interval "10m0s"; url "https://cloudflare.github.io/helm-charts" ] ]
        |> Yzl.render

module HelmRelease =
    let apiVersion = Yzl.str
    let kind = Yzl.str
    let metadata = Yzl.map
    let name = Yzl.str
    let ``namespace`` = Yzl.str
    let spec = Yzl.map
    let chart = Yzl.map
    let sourceRef = Yzl.map
    //let version = Yzl.str
    let interval = Yzl.str
    let releaseName = Yzl.str
    let targetNamespace = Yzl.str
    let values = Yzl.map
    let postRenderers = Yzl.seq
    let kustomize = Yzl.map

    let cloudflareTunnel valuesInline =
        let patches =
            [ "patches"
              .= [ [ "target" .= [ "kind" .= "Deployment"; "name" .= "cloudflare-tunnel" ]
                     "patch"
                     .= !| """
                        - op: replace
                          path: /spec/template/spec/volumes/0
                          value:
                            name: creds
                            csi:
                              driver: secrets-store.csi.k8s.io
                              readOnly: true
                              volumeAttributes:
                                secretProviderClass: tunnel-secret
                        """ ] ] ]

        [ apiVersion "helm.toolkit.fluxcd.io/v2beta1"
          kind "HelmRelease"
          metadata [ name "cloudflare-tunnel"; ``namespace`` "cluster-config" ]
          spec
              [ chart
                    [ spec
                          [ "chart" .= "cloudflare-tunnel"
                            sourceRef [ kind "HelmRepository"; name "cloudflare" ] ] ]
                interval "10m0s"
                releaseName "cloudflare-tunnel"
                targetNamespace "cloudflare"
                values valuesInline
                postRenderers [ kustomize patches ] ] ]
        |> Yzl.render

module SecretProviderClass =
    let apiVersion = Yzl.str
    let kind = Yzl.str
    let metadata = Yzl.map
    let name = Yzl.str
    let ``namespace`` = Yzl.str
    let spec = Yzl.map
    let provider = Yzl.str
    let parameters = Yzl.map
    let clientID = Yzl.str
    let keyvaultName = Yzl.str
    let objects = Yzl.str
    let tenantID = Yzl.str

    let cloudflareTunnel (clientId: string) (keyVaultName: string) (tenantId: string) =
        [ apiVersion "secrets-store.csi.x-k8s.io/v1"
          kind "SecretProviderClass"
          metadata [ name "tunnel-secret"; ``namespace`` "cloudflare" ]
          spec
              [ provider "azure"
                parameters
                    [ clientID clientId
                      keyvaultName keyVaultName
                      tenantID tenantId
                      objects
                          !| """
                        array:
                          - |
                            objectName: tunnel-secret
                            objectAlias: credentials.json
                            objectType: secret
                        """ ] ] ]
        |> Yzl.render
