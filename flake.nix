{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
    let
      pkgs = import nixpkgs {
        inherit system;
      };
    in {
      devShells.default = pkgs.mkShell {
        packages = [
          pkgs.k9s
          pkgs.kubernetes-helm
          pkgs.kubectl
          pkgs.pulumi-bin
        ];
        __ETC_PROFILE_NIX_SOURCED=1;
        shellHook = ''
          export PATH=~/.pulumi/plugins/resource-kubernetes-v4.6.1:~/.pulumi/plugins/resource-tls-v5.0.0:~/.pulumi/plugins/resource-random-v4.15.0:$PATH
        '';
      };
    });
}
