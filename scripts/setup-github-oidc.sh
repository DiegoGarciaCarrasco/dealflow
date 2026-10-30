#!/usr/bin/env bash
# =============================================================================
#  One-time setup: lets GitHub Actions deploy DealFlow to Azure with NO secrets.
#
#  Creates an Entra ID app registration whose only way in is a GitHub OIDC token
#  issued to this repo's "dev" environment (federated credential), then grants it
#  the roles Terraform needs. Prints the 7 GitHub repository variables to add.
#
#  Usage (from the repo root, signed in with `az login` as a subscription Owner):
#    ./scripts/setup-github-oidc.sh <github-owner/repo> <tfstate-resource-group> <tfstate-storage-account>
#  Example:
#    ./scripts/setup-github-oidc.sh DiegoGarciaCarrasco/dealflow rg-tfstate sadealflowtfstate
#
#  Safe to re-run: it reuses the app registration and skips existing role assignments.
# =============================================================================
set -euo pipefail

REPO="${1:?Pass the GitHub repo as owner/name, e.g. DiegoGarciaCarrasco/dealflow}"
TFSTATE_RG="${2:?Pass the Terraform state resource group, e.g. rg-tfstate}"
TFSTATE_ACCOUNT="${3:?Pass the Terraform state storage account name}"
APP_NAME="github-dealflow-deployer"

SUB_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)
SUB_SCOPE="/subscriptions/$SUB_ID"
STATE_SCOPE=$(az storage account show -g "$TFSTATE_RG" -n "$TFSTATE_ACCOUNT" --query id -o tsv)

echo "Subscription: $SUB_ID"
echo "Repository:   $REPO (environment: dev)"
echo

# 1. App registration + service principal ------------------------------------
APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv)
if [ -z "$APP_ID" ]; then
  APP_ID=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv)
  echo "Created app registration $APP_NAME"
else
  echo "Reusing app registration $APP_NAME"
fi
SP_ID=$(az ad sp show --id "$APP_ID" --query id -o tsv 2>/dev/null || az ad sp create --id "$APP_ID" --query id -o tsv)

# 2. Federated credential: trust tokens GitHub issues to jobs in this repo's "dev" environment
SUBJECT="repo:${REPO}:environment:dev"
if ! az ad app federated-credential list --id "$APP_ID" --query "[].subject" -o tsv | grep -qx "$SUBJECT"; then
  az ad app federated-credential create --id "$APP_ID" --parameters "{
    \"name\": \"github-dev\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"$SUBJECT\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }" > /dev/null
  echo "Added federated credential for $SUBJECT"
fi

# 3. Roles ---------------------------------------------------------------------
grant() {  # principal-id principal-type role scope
  if [ -z "$(az role assignment list --assignee "$1" --role "$3" --scope "$4" --query "[0].id" -o tsv)" ]; then
    az role assignment create --assignee-object-id "$1" --assignee-principal-type "$2" --role "$3" --scope "$4" > /dev/null
    echo "  granted  $3"
  else
    echo "  exists   $3"
  fi
}
echo "Roles for the GitHub Actions identity:"
grant "$SP_ID" ServicePrincipal "Contributor"                   "$SUB_SCOPE"    # create/update resources
grant "$SP_ID" ServicePrincipal "User Access Administrator"     "$SUB_SCOPE"    # Terraform assigns roles to the apps' identities
grant "$SP_ID" ServicePrincipal "Storage Blob Data Contributor" "$STATE_SCOPE"  # read/write Terraform state (Entra auth)
grant "$SP_ID" ServicePrincipal "App Configuration Data Owner"  "$SUB_SCOPE"    # read/write the AiEnrichment flag

echo "Roles for you (so local terraform keeps working after the change):"
ME=$(az ad signed-in-user show --query id -o tsv)
grant "$ME" User "App Configuration Data Owner" "$SUB_SCOPE"

# 4. What to paste into GitHub ---------------------------------------------------
cat <<EOF

Done. In GitHub: Settings → Environments → New environment → name it: dev

Settings → Secrets and variables → Actions → Secrets tab → New repository secret
(secrets are masked as *** in Actions logs, which are PUBLIC on a public repo):

  AZURE_CLIENT_ID        $APP_ID
  AZURE_TENANT_ID        $TENANT_ID
  AZURE_SUBSCRIPTION_ID  $SUB_ID
  ADMIN_OBJECT_ID        $ME        (optional: your portal data access)

Settings → Secrets and variables → Actions → Variables tab → New repository variable:

  TFSTATE_RG             $TFSTATE_RG
  TFSTATE_ACCOUNT        $TFSTATE_ACCOUNT
  TFSTATE_CONTAINER      <container_name from infra/backend.hcl>
  TFSTATE_KEY            <key from infra/backend.hcl>

TFSTATE_CONTAINER and TFSTATE_KEY MUST match your infra/backend.hcl, so the pipeline manages
the environment you already have instead of creating a second copy of it.

For local runs, put the same admin ID in infra/admin.auto.tfvars (gitignored):
  echo 'admin_object_id = "$ME"' > infra/admin.auto.tfvars
EOF
