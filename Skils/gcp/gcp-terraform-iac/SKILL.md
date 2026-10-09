---
name: gcp-terraform-iac
description: >-
  Write and operate Terraform for Google Cloud: repository layout (environments and modules),
  remote state in GCS, provider pinning, naming and labels, least-privilege service accounts and
  IAM bindings, Workload Identity Federation for CI, resources for Cloud Run v2, Cloud SQL,
  Secret Manager, Artifact Registry, VPC and Cloud Storage, plan review discipline, importing
  existing resources, drift detection, tflint/checkov/validate in CI and a plan-in-PR /
  apply-on-merge workflow. Use this skill whenever the user touches a .tf file, says "terraform",
  "infra as code", "IaC", "google provider", "google_cloud_run_v2_service", "tfstate", "import
  this bucket into terraform", "give the service account access to X", asks to create or change
  any GCP resource that should outlive a gcloud command, or asks how to review an infrastructure
  PR, even if they only say "add a bucket" or "the pipeline needs permissions".
---

# Terraform for Google Cloud

This skill produces infrastructure that is reproducible across environments, reviewable in a pull
request, and safe to apply: a `terraform plan` that a reviewer can read in two minutes, state that
cannot be corrupted by two engineers applying at once, and IAM that grants exactly what a
workload needs. The common failures are not syntax errors; they are an authoritative IAM
resource that silently removes someone else's access, a `-/+` replace of a Cloud SQL instance
hidden in a 200-line plan, and a CI service account with `roles/editor` because it was easier.

## Workflow

1. **Read the existing layout, versions and conventions first** (`versions.tf`, `backend.tf`,
   module structure, naming of existing resources, labels). A new module that invents its own
   naming scheme becomes permanent technical debt because renaming GCP resources means
   recreating them.
2. **Decide what owns the resource**: Terraform, the deploy pipeline, or an operator. Cloud Run
   image tags and Secret Manager *versions* usually belong to the pipeline, the service and the
   secret *container* belong to Terraform. Write that decision down in the module README and
   enforce it with `lifecycle { ignore_changes }`.
3. **Write the resource in a module with typed, validated variables**, then call it from the
   environment root. Keep environment roots to module calls and values; logic lives in modules.
4. **Run `terraform fmt`, `terraform validate` and `tflint` locally**, then `terraform plan`
   and read the plan line by line. Search for `must be replaced`, `destroy`, and any IAM change
   you did not intend. Stateful resources (Cloud SQL, buckets with data) carry
   `deletion_protection` / `prevent_destroy` so a bad plan fails instead of applying.
5. **Open a PR with the plan output attached**; apply only from CI on merge (or from a protected
   environment) so the applied change equals the reviewed change.
6. **Verify**: a second `terraform plan` after apply shows `No changes`; the resource looks right
   in `gcloud ... describe`; the workload can actually use it (a Cloud Run revision that reads the
   new secret, a connection to the new database).

## Repository layout

```
infra/
├── bootstrap/                # state bucket, WIF pool, CI service account; applied once by a human
├── modules/
│   ├── cloud-run-service/    # main.tf, variables.tf, outputs.tf, versions.tf, README.md
│   ├── cloud-sql-postgres/
│   ├── gcs-bucket/
│   └── wif-github/
└── envs/
    ├── dev/                  # backend.tf, providers.tf, main.tf, variables.tf, terraform.tfvars
    ├── test/
    └── prod/
```

One root per environment, each with its own state prefix and its own CI credentials, instead of
Terraform workspaces: workspaces share a backend and provider config, so `terraform apply` in the
wrong workspace is one forgotten `select` away. Separate GCP projects per environment keep IAM and
quotas isolated as well; a single project with an `env` label is the compromise for small teams.

## State, versions and provider

```hcl
# envs/prod/backend.tf
terraform {
  backend "gcs" {
    bucket = "acme-tfstate"        # created in bootstrap/, versioning on, uniform access, no public access
    prefix = "envs/prod"
  }
}

# versions.tf (root and every module)
terraform {
  required_version = "~> 1.9"
  required_providers {
    google      = { source = "hashicorp/google", version = "~> 6.0" }
    google-beta = { source = "hashicorp/google-beta", version = "~> 6.0" }
  }
}

# envs/prod/providers.tf
provider "google" {
  project        = var.project_id
  region         = var.region
  default_labels = { env = "prod", app = var.app, managed-by = "terraform" }
}
```

- The GCS backend locks state per object, so concurrent applies fail fast instead of corrupting
  state. Enable bucket versioning to recover from a broken state file.
- Commit `.terraform.lock.hcl`; upgrade providers deliberately with `terraform init -upgrade` in
  its own PR, because major provider versions change defaults (6.x turned on
  `deletion_protection` for Cloud Run v2 services, for example).
- `default_labels` on the provider labels every resource that supports labels, which is what
  makes billing export by `env`/`app` work without touching each resource.
- Never store `terraform.tfvars` with secrets; state itself contains every attribute, so treat the
  state bucket as sensitive (restrict to the CI service account and a break-glass group).

## Naming and labels

Pattern `<app>-<component>-<env>` for everything (`shop-api-prod`, `shop-pg-prod`,
`shop-uploads-prod`); bucket names are global, so prefix them with the project or organisation.
Labels: `env`, `app`, `owner` (team), `cost-center`, `managed-by`. Lowercase, hyphens, no dots.
Service accounts: `<app>-<component>-sa` with a `display_name` that says what runs as it.

## IAM: least privilege

```hcl
resource "google_service_account" "api" {
  account_id   = "shop-api-sa"
  display_name = "Cloud Run service shop-api"
}

# Additive: coexists with bindings made elsewhere
resource "google_project_iam_member" "api_cloudsql" {
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = google_service_account.api.member
}

# Scope to the resource, not the project, when the resource supports it
resource "google_secret_manager_secret_iam_member" "api_db_secret" {
  secret_id = google_secret_manager_secret.db_connection.secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = google_service_account.api.member
}

resource "google_cloud_run_v2_service_iam_member" "frontend_invokes_api" {
  name     = google_cloud_run_v2_service.api.name
  location = var.region
  role     = "roles/run.invoker"
  member   = google_service_account.frontend.member
}
```

- Use `*_iam_member` (additive). `*_iam_policy` is authoritative for the whole resource and
  `*_iam_binding` is authoritative for one role: both remove any member not in your code,
  including bindings created by Google-managed service agents, which breaks things in ways that
  are hard to trace back to Terraform.
- `roles/owner` and `roles/editor` are never the answer for a workload or a pipeline. Compose
  predefined roles (`run.developer`, `artifactregistry.writer`, `cloudsql.client`,
  `secretmanager.secretAccessor`, `storage.objectUser`); create a custom role only when a
  predefined one grants a dangerous permission you cannot avoid.
- `roles/iam.serviceAccountUser` on a *specific* service account is what lets CI deploy a Cloud
  Run service that runs as that account. Grant it on the account, not the project.
- The default compute service account is Editor on the project; never attach it to Cloud Run.

## Workload Identity Federation for CI

```hcl
resource "google_iam_workload_identity_pool" "github" {
  workload_identity_pool_id = "github"
}

resource "google_iam_workload_identity_pool_provider" "github" {
  workload_identity_pool_id          = google_iam_workload_identity_pool.github.workload_identity_pool_id
  workload_identity_pool_provider_id = "github"
  attribute_mapping = {
    "google.subject"             = "assertion.sub"
    "attribute.repository"       = "assertion.repository"
    "attribute.repository_owner" = "assertion.repository_owner"
  }
  attribute_condition = "assertion.repository_owner == \"${var.github_org}\""
  oidc { issuer_uri = "https://token.actions.githubusercontent.com" }
}

resource "google_service_account_iam_member" "ci_wif" {
  service_account_id = google_service_account.ci.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github.name}/attribute.repository/${var.github_org}/${var.github_repo}"
}
```

No key files anywhere: GitHub exchanges its OIDC token for a short-lived Google token. The
`attribute_condition` is mandatory (Google rejects GitHub providers without one) and the
`principalSet` member pins impersonation to one repository. Give the Terraform CI account
`roles/storage.objectAdmin` on the state bucket plus the roles needed to manage the resources it
owns; give the deploy CI account only `run.developer` + `artifactregistry.writer`.

## Core resources

Cloud Run v2 (abridged; full module in `references/module-skeletons.md`):

```hcl
resource "google_cloud_run_v2_service" "api" {
  name                = "shop-api-${var.env}"
  location            = var.region
  ingress             = "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER"
  deletion_protection = var.env == "prod"

  template {
    service_account                  = google_service_account.api.email
    max_instance_request_concurrency = 80
    timeout                          = "60s"
    scaling { min_instance_count = var.min_instances; max_instance_count = 10 }
    vpc_access {
      network_interfaces { network = var.network; subnetwork = var.subnet }
      egress = "PRIVATE_RANGES_ONLY"
    }
    volumes {
      name = "cloudsql"
      cloud_sql_instance { instances = [google_sql_database_instance.main.connection_name] }
    }
    containers {
      image = var.image
      ports { container_port = 8080 }
      resources { limits = { cpu = "1", memory = "512Mi" }; cpu_idle = true; startup_cpu_boost = true }
      env { name = "ASPNETCORE_ENVIRONMENT"; value = "Production" }
      env {
        name = "ConnectionStrings__Default"
        value_source { secret_key_ref { secret = google_secret_manager_secret.db_connection.secret_id; version = "latest" } }
      }
      volume_mounts { name = "cloudsql"; mount_path = "/cloudsql" }
      startup_probe { http_get { path = "/health/startup" }; period_seconds = 2; failure_threshold = 30 }
      liveness_probe { http_get { path = "/health/live" }; period_seconds = 30 }
    }
  }
  lifecycle { ignore_changes = [template[0].containers[0].image, client, client_version] }
}
```

Cloud SQL for PostgreSQL (private IP, HA, PITR; operational details in `gcp-data-services`):

```hcl
resource "google_sql_database_instance" "main" {
  name                = "shop-pg-${var.env}"
  database_version    = "POSTGRES_16"
  region              = var.region
  deletion_protection = true
  settings {
    tier              = var.env == "prod" ? "db-custom-2-7680" : "db-custom-1-3840"
    edition           = "ENTERPRISE"
    availability_type = var.env == "prod" ? "REGIONAL" : "ZONAL"
    disk_autoresize   = true
    ip_configuration {
      ipv4_enabled    = false
      private_network = var.network_id
    }
    backup_configuration {
      enabled                        = true
      point_in_time_recovery_enabled = true
      start_time                     = "02:00"
      transaction_log_retention_days = 7
      backup_retention_settings { retained_backups = 14 }
    }
    maintenance_window { day = 7; hour = 3; update_track = "stable" }
    database_flags { name = "cloudsql.iam_authentication"; value = "on" }
    database_flags { name = "log_min_duration_statement"; value = "1000" }
    insights_config { query_insights_enabled = true; record_application_tags = true }
  }
  depends_on = [google_service_networking_connection.private_service_access]
}
```

Other building blocks:

- **Secret Manager**: `google_secret_manager_secret` with `replication { auto {} }` in Terraform;
  add the value with `gcloud secrets versions add` or from CI, not as a
  `google_secret_manager_secret_version` with a literal (it lands in state and in the PR).
- **Artifact Registry**: `google_artifact_registry_repository` (`format = "DOCKER"`) with
  `cleanup_policies` (keep last N tagged, delete untagged older than 7 days) and
  `cleanup_policy_dry_run = false` once verified.
- **VPC**: `google_compute_network` (`auto_create_subnetworks = false`), a dedicated
  `google_compute_subnetwork` for Direct VPC egress (at least /26, Cloud Run takes addresses
  from it), `google_compute_global_address` (`purpose = "VPC_PEERING"`) plus
  `google_service_networking_connection` for Cloud SQL private IP. Use
  `google_vpc_access_connector` only where Direct VPC egress is unavailable.
- **Cloud Storage**: `uniform_bucket_level_access = true`, `public_access_prevention = "enforced"`,
  `versioning { enabled = true }` for anything users upload, `lifecycle_rule` to delete old
  noncurrent versions or move cold data to `NEARLINE`, `force_destroy = false` in prod.

## Plan review discipline

- `terraform plan -out=tfplan` then `terraform apply tfplan`: the apply is exactly the reviewed
  plan, not a fresh one computed later against a changed world.
- Read the summary first (`Plan: 2 to add, 1 to change, 1 to destroy`), then every `-/+` and
  `-` block. A replace of a Cloud SQL instance, a bucket or a service account is data loss or an
  outage; find the attribute that forces it (`# forces replacement`) and decide explicitly.
- IAM diffs deserve a second reader: a removed `member` line is somebody's access disappearing.
- `-target` and `-replace` are for incident recovery, not routine; a plan that only works with
  `-target` means the graph has a problem worth fixing.
- Guard rails: `prevent_destroy = true` in `lifecycle` for stateful resources, and
  `deletion_protection` where the provider offers it.

## Importing, refactoring, drift

- Import with `import` blocks (Terraform 1.5+), reviewed like any change:
  `import { to = google_storage_bucket.uploads, id = "acme-shop-uploads-prod" }` and
  `terraform plan -generate-config-out=generated.tf` to draft the resource, then tidy it into
  the module. Expect a few attribute diffs on the first plan; align the code, do not apply
  blindly.
- Move resources without recreating them using `moved { from = ..., to = ... }` blocks when you
  restructure modules; `terraform state mv` works too but is not reviewable in a PR.
- Drift: a nightly CI job runs `terraform plan -detailed-exitcode` per environment; exit code 2
  means somebody clicked in the console. Post the plan to the team channel and either revert the
  manual change or codify it. Drift that is tolerated becomes the real configuration.

## CI for Terraform

On every PR: `terraform fmt -check -recursive`, `terraform init -backend=false` +
`terraform validate` per root, `tflint --recursive` with the `google` ruleset (catches invalid
machine types, regions, deprecated attributes), and a policy scan (`checkov -d infra
--framework terraform` or `trivy config infra`) for public buckets, missing deletion protection
and wide IAM. Then `terraform plan` with the environment's WIF identity and the plan posted as a
PR comment. On merge to `main`: `terraform apply` in a protected environment requiring approval
for `prod`. The full workflow is in `references/module-skeletons.md`.

## Examples

**Example 1**
Input: "The API needs to read the Stripe key from Secret Manager."
Output: `google_secret_manager_secret.stripe_key` (no version resource), a
`google_secret_manager_secret_iam_member` granting `secretAccessor` to the API's service account
only, an `env` block with `value_source.secret_key_ref` in the Cloud Run module, and a note to
add the version with `gcloud secrets versions add stripe-key --data-file=-`.

**Example 2**
Input: "Plan shows the Cloud SQL instance will be replaced after I changed the tier."
Output: Tier changes are in-place; something else forces replacement, usually `name`, `region`
or `database_version`. Find `# forces replacement` in the plan, revert that attribute, and
confirm `deletion_protection = true` would have blocked the apply anyway.

## References

- `references/module-skeletons.md` — read when creating a new module or environment root, when
  writing the Terraform CI workflow, or when you need the complete `cloud-run-service` and
  `wif-github` modules with variables and outputs.
