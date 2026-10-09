# Terraform module skeletons and CI workflow

Read this when creating a module or environment root from scratch, or when writing the
plan-in-PR / apply-on-merge pipeline. Everything targets the `hashicorp/google` 6.x provider.

## modules/cloud-run-service

`versions.tf`

```hcl
terraform {
  required_version = "~> 1.9"
  required_providers {
    google = { source = "hashicorp/google", version = "~> 6.0" }
  }
}
```

`variables.tf`

```hcl
variable "name"       { type = string }
variable "project_id" { type = string }
variable "region"     { type = string }
variable "env" {
  type = string
  validation {
    condition     = contains(["dev", "test", "prod"], var.env)
    error_message = "env must be dev, test or prod."
  }
}
variable "image" {
  type        = string
  description = "Initial image. Later images are deployed by CI; Terraform ignores changes to this field."
}
variable "service_account_email" { type = string }
variable "env_vars"        { type = map(string); default = {} }
variable "secret_env_vars" {
  type        = map(string)
  default     = {}
  description = "ENV_NAME => Secret Manager secret_id (latest version is used)."
}
variable "cloudsql_connection_names" { type = list(string); default = [] }
variable "vpc" {
  type    = object({ network = string, subnet = string, egress = optional(string, "PRIVATE_RANGES_ONLY") })
  default = null
}
variable "cpu"             { type = string; default = "1" }
variable "memory"          { type = string; default = "512Mi" }
variable "concurrency"     { type = number; default = 80 }
variable "min_instances"   { type = number; default = 0 }
variable "max_instances"   { type = number; default = 10 }
variable "timeout_seconds" { type = number; default = 60 }
variable "cpu_always_allocated" { type = bool; default = false }
variable "ingress" {
  type    = string
  default = "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER"
}
variable "invokers" {
  type        = list(string)
  default     = []
  description = "IAM members allowed to invoke, e.g. serviceAccount:x@p.iam.gserviceaccount.com or allUsers."
}
variable "labels" { type = map(string); default = {} }
```

`main.tf`

```hcl
resource "google_cloud_run_v2_service" "this" {
  name                = var.name
  project             = var.project_id
  location            = var.region
  ingress             = var.ingress
  deletion_protection = var.env == "prod"
  labels              = var.labels

  template {
    service_account                  = var.service_account_email
    max_instance_request_concurrency = var.concurrency
    timeout                          = "${var.timeout_seconds}s"
    execution_environment            = "EXECUTION_ENVIRONMENT_GEN2"

    scaling {
      min_instance_count = var.min_instances
      max_instance_count = var.max_instances
    }

    dynamic "vpc_access" {
      for_each = var.vpc == null ? [] : [var.vpc]
      content {
        egress = vpc_access.value.egress
        network_interfaces {
          network    = vpc_access.value.network
          subnetwork = vpc_access.value.subnet
        }
      }
    }

    dynamic "volumes" {
      for_each = length(var.cloudsql_connection_names) > 0 ? [1] : []
      content {
        name = "cloudsql"
        cloud_sql_instance { instances = var.cloudsql_connection_names }
      }
    }

    containers {
      image = var.image
      ports { container_port = 8080 }

      resources {
        limits            = { cpu = var.cpu, memory = var.memory }
        cpu_idle          = !var.cpu_always_allocated
        startup_cpu_boost = true
      }

      dynamic "env" {
        for_each = var.env_vars
        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = var.secret_env_vars
        content {
          name = env.key
          value_source {
            secret_key_ref {
              secret  = env.value
              version = "latest"
            }
          }
        }
      }

      dynamic "volume_mounts" {
        for_each = length(var.cloudsql_connection_names) > 0 ? [1] : []
        content {
          name       = "cloudsql"
          mount_path = "/cloudsql"
        }
      }

      startup_probe {
        http_get { path = "/health/startup"; port = 8080 }
        initial_delay_seconds = 0
        period_seconds        = 2
        timeout_seconds       = 1
        failure_threshold     = 30
      }

      liveness_probe {
        http_get { path = "/health/live"; port = 8080 }
        period_seconds    = 30
        failure_threshold = 3
      }
    }
  }

  traffic {
    type    = "TRAFFIC_TARGET_ALLOCATION_TYPE_LATEST"
    percent = 100
  }

  lifecycle {
    # CI owns the image and traffic split; Terraform owns everything else.
    ignore_changes = [template[0].containers[0].image, traffic, client, client_version]
  }
}

resource "google_cloud_run_v2_service_iam_member" "invokers" {
  for_each = toset(var.invokers)
  project  = var.project_id
  location = var.region
  name     = google_cloud_run_v2_service.this.name
  role     = "roles/run.invoker"
  member   = each.value
}

# Secret access for each referenced secret
resource "google_secret_manager_secret_iam_member" "secrets" {
  for_each  = var.secret_env_vars
  project   = var.project_id
  secret_id = each.value
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${var.service_account_email}"
}
```

`outputs.tf`

```hcl
output "name" { value = google_cloud_run_v2_service.this.name }
output "uri"  { value = google_cloud_run_v2_service.this.uri }
```

Design notes:

- `ignore_changes` on `traffic` lets `gcloud run services update-traffic` manage canaries; if the
  team prefers Terraform-driven traffic, remove it and expose a `traffic` variable instead.
- The module grants secret access itself so a caller cannot forget it; the matching Cloud SQL
  role (`roles/cloudsql.client`) is project-level and belongs with the service account module.
- The initial `image` can be a tiny placeholder (`us-docker.pkg.dev/cloudrun/container/hello`)
  on first creation; CI replaces it on the first deploy.

## modules/wif-github

```hcl
variable "project_id"  { type = string }
variable "github_org"  { type = string }
variable "repos"       { type = map(string) }   # repo name => service account email allowed for it

resource "google_iam_workload_identity_pool" "github" {
  project                   = var.project_id
  workload_identity_pool_id = "github"
  display_name              = "GitHub Actions"
}

resource "google_iam_workload_identity_pool_provider" "github" {
  project                            = var.project_id
  workload_identity_pool_id          = google_iam_workload_identity_pool.github.workload_identity_pool_id
  workload_identity_pool_provider_id = "github"
  attribute_mapping = {
    "google.subject"             = "assertion.sub"
    "attribute.repository"       = "assertion.repository"
    "attribute.repository_owner" = "assertion.repository_owner"
    "attribute.ref"              = "assertion.ref"
  }
  attribute_condition = "assertion.repository_owner == \"${var.github_org}\""
  oidc { issuer_uri = "https://token.actions.githubusercontent.com" }
}

resource "google_service_account_iam_member" "repo_can_impersonate" {
  for_each           = var.repos
  service_account_id = "projects/${var.project_id}/serviceAccounts/${each.value}"
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github.name}/attribute.repository/${var.github_org}/${each.key}"
}

output "provider_name" { value = google_iam_workload_identity_pool_provider.github.name }
```

To restrict production deploys to the `main` branch, use
`attribute.ref/refs/heads/main` in a second, more specific principalSet member for the prod
deploy account.

## Environment root (envs/prod/main.tf)

```hcl
module "network" {
  source     = "../../modules/network"
  project_id = var.project_id
  region     = var.region
  env        = "prod"
}

module "db" {
  source     = "../../modules/cloud-sql-postgres"
  project_id = var.project_id
  region     = var.region
  env        = "prod"
  name       = "shop-pg-prod"
  network_id = module.network.network_id
  tier       = "db-custom-2-7680"
  ha         = true
}

resource "google_service_account" "api" {
  account_id   = "shop-api-sa"
  display_name = "Cloud Run service shop-api (prod)"
}

resource "google_project_iam_member" "api_cloudsql" {
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = google_service_account.api.member
}

module "api" {
  source                    = "../../modules/cloud-run-service"
  project_id                = var.project_id
  region                    = var.region
  env                       = "prod"
  name                      = "shop-api-prod"
  image                     = "us-docker.pkg.dev/cloudrun/container/hello"
  service_account_email     = google_service_account.api.email
  cloudsql_connection_names = [module.db.connection_name]
  vpc                       = { network = module.network.network_id, subnet = module.network.serverless_subnet_id }
  min_instances             = 1
  concurrency               = 40
  env_vars = {
    ASPNETCORE_ENVIRONMENT = "Production"
    GOOGLE_CLOUD_PROJECT   = var.project_id
  }
  secret_env_vars = {
    ConnectionStrings__Default = module.db.connection_secret_id
  }
  invokers = ["serviceAccount:${google_service_account.frontend.email}"]
  labels   = { app = "shop", env = "prod", owner = "team-shop" }
}
```

## CI workflow: plan in PR, apply on merge

`.github/workflows/terraform.yml`

```yaml
name: terraform
on:
  pull_request: { paths: ["infra/**"] }
  push: { branches: [main], paths: ["infra/**"] }

permissions: { contents: read, id-token: write, pull-requests: write }

env:
  TF_IN_AUTOMATION: "true"
  TF_INPUT: "false"

jobs:
  checks:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: hashicorp/setup-terraform@v3
        with: { terraform_version: "1.9.x" }
      - run: terraform fmt -check -recursive infra
      - uses: terraform-linters/setup-tflint@v4
      - run: tflint --init && tflint --recursive --chdir=infra
      - uses: bridgecrewio/checkov-action@v12
        with: { directory: infra, framework: terraform, quiet: true }

  plan:
    needs: checks
    runs-on: ubuntu-latest
    strategy:
      matrix: { env: [dev, prod] }
    steps:
      - uses: actions/checkout@v4
      - uses: google-github-actions/auth@v2
        with:
          workload_identity_provider: ${{ vars.WIF_PROVIDER }}
          service_account: terraform-${{ matrix.env }}@${{ vars.PROJECT_ID }}.iam.gserviceaccount.com
      - uses: hashicorp/setup-terraform@v3
        with: { terraform_version: "1.9.x" }
      - working-directory: infra/envs/${{ matrix.env }}
        run: terraform init && terraform validate
      - id: plan
        working-directory: infra/envs/${{ matrix.env }}
        run: terraform plan -no-color -out=tfplan -detailed-exitcode 2>&1 | tee plan.txt
        continue-on-error: true
      - uses: actions/upload-artifact@v4
        with: { name: tfplan-${{ matrix.env }}-${{ github.sha }}, path: infra/envs/${{ matrix.env }}/tfplan }
      - if: github.event_name == 'pull_request'
        uses: actions/github-script@v7
        with:
          script: |
            const fs = require('fs');
            const plan = fs.readFileSync('infra/envs/${{ matrix.env }}/plan.txt', 'utf8').slice(-60000);
            await github.rest.issues.createComment({
              ...context.repo, issue_number: context.issue.number,
              body: `### terraform plan (${{ matrix.env }})\n\`\`\`\n${plan}\n\`\`\``
            });

  apply:
    if: github.event_name == 'push'
    needs: plan
    runs-on: ubuntu-latest
    strategy:
      matrix: { env: [dev, prod] }
      max-parallel: 1
    environment: terraform-${{ matrix.env }}      # prod requires reviewers
    steps:
      - uses: actions/checkout@v4
      - uses: google-github-actions/auth@v2
        with:
          workload_identity_provider: ${{ vars.WIF_PROVIDER }}
          service_account: terraform-${{ matrix.env }}@${{ vars.PROJECT_ID }}.iam.gserviceaccount.com
      - uses: hashicorp/setup-terraform@v3
        with: { terraform_version: "1.9.x" }
      - uses: actions/download-artifact@v4
        with: { name: tfplan-${{ matrix.env }}-${{ github.sha }}, path: infra/envs/${{ matrix.env }} }
      - working-directory: infra/envs/${{ matrix.env }}
        run: terraform init && terraform apply -no-color tfplan
```

`.tflint.hcl` at the repository root:

```hcl
plugin "google" {
  enabled = true
  version = "0.30.0"
  source  = "github.com/terraform-linters/tflint-ruleset-google"
}
rule "terraform_required_providers" { enabled = true }
rule "terraform_naming_convention"  { enabled = true }
```

Applying the saved `tfplan` artifact (not a fresh plan) guarantees the applied change is the one
that was reviewed; if the world moved in between, Terraform refuses the stale plan and the PR is
re-run. One Terraform service account per environment means the dev pipeline physically cannot
touch prod state.

## Nightly drift check

```yaml
on: { schedule: [{ cron: "0 4 * * 1-5" }] }
# ...auth + init as above...
- run: terraform plan -detailed-exitcode -no-color || echo "DRIFT=$?" >> "$GITHUB_ENV"
- if: env.DRIFT == '2'
  run: echo "::error::Drift detected in ${{ matrix.env }}; see plan output"
```

Exit code 2 means changes are pending: either someone changed the console or a new provider
version changed a default. Treat both as work items, not noise.
