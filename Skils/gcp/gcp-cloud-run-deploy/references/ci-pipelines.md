# CI/CD pipelines for Cloud Run

Read this when writing or reviewing a complete deploy pipeline: GitHub Actions with Workload
Identity Federation, or Cloud Build. Both pipelines follow the same order: build and push the
image, run the migration job, deploy a no-traffic canary, smoke-test it, promote.

## One-off bootstrap of Workload Identity Federation (gcloud)

Prefer the Terraform version in `gcp-terraform-iac`; the commands below are the equivalent for a
first experiment.

```bash
PROJECT=my-project; PROJECT_NUMBER=$(gcloud projects describe $PROJECT --format='value(projectNumber)')
ORG=my-github-org; REPO=my-repo

gcloud iam service-accounts create ci-deploy --display-name="CI deploy"
gcloud iam workload-identity-pools create github --location=global --display-name="GitHub Actions"
gcloud iam workload-identity-pools providers create-oidc github \
  --location=global --workload-identity-pool=github \
  --issuer-uri="https://token.actions.githubusercontent.com" \
  --attribute-mapping="google.subject=assertion.sub,attribute.repository=assertion.repository,attribute.repository_owner=assertion.repository_owner" \
  --attribute-condition="assertion.repository_owner == '$ORG'"

# Only this repository may impersonate the CI service account
gcloud iam service-accounts add-iam-policy-binding ci-deploy@$PROJECT.iam.gserviceaccount.com \
  --role=roles/iam.workloadIdentityUser \
  --member="principalSet://iam.googleapis.com/projects/$PROJECT_NUMBER/locations/global/workloadIdentityPools/github/attribute.repository/$ORG/$REPO"

for role in roles/artifactregistry.writer roles/run.developer; do
  gcloud projects add-iam-policy-binding $PROJECT --member="serviceAccount:ci-deploy@$PROJECT.iam.gserviceaccount.com" --role=$role
done
# Allow CI to deploy a service that runs as the runtime SA
gcloud iam service-accounts add-iam-policy-binding api-sa@$PROJECT.iam.gserviceaccount.com \
  --member="serviceAccount:ci-deploy@$PROJECT.iam.gserviceaccount.com" --role=roles/iam.serviceAccountUser
```

The attribute condition is required: without it any GitHub repository in the world could request
a token from the pool. Scoping by `attribute.repository` on the binding is what stops a fork from
deploying.

## GitHub Actions workflow

`.github/workflows/deploy-api.yml`:

```yaml
name: deploy-api
on:
  push:
    branches: [main]
    paths: ["src/**", "Dockerfile", ".github/workflows/deploy-api.yml"]
  workflow_dispatch:

env:
  PROJECT_ID: my-project
  REGION: europe-west1
  REPO: europe-west1-docker.pkg.dev/my-project/apps
  SERVICE: api
  WIF_PROVIDER: projects/123456789/locations/global/workloadIdentityPools/github/providers/github
  CI_SA: ci-deploy@my-project.iam.gserviceaccount.com

concurrency: { group: deploy-api, cancel-in-progress: false }

jobs:
  build:
    runs-on: ubuntu-latest
    permissions: { contents: read, id-token: write }
    outputs: { image: ${{ steps.meta.outputs.image }} }
    steps:
      - uses: actions/checkout@v4
      - id: meta
        run: echo "image=$REPO/$SERVICE:${GITHUB_SHA}" >> "$GITHUB_OUTPUT"
      - id: auth
        uses: google-github-actions/auth@v2
        with:
          workload_identity_provider: ${{ env.WIF_PROVIDER }}
          service_account: ${{ env.CI_SA }}
          token_format: access_token
      - uses: docker/login-action@v3
        with:
          registry: europe-west1-docker.pkg.dev
          username: oauth2accesstoken
          password: ${{ steps.auth.outputs.access_token }}
      - uses: docker/setup-buildx-action@v3
      - uses: docker/build-push-action@v6
        with:
          context: .
          file: Dockerfile
          push: true
          tags: ${{ steps.meta.outputs.image }}
          cache-from: type=registry,ref=${{ env.REPO }}/${{ env.SERVICE }}:buildcache
          cache-to: type=registry,ref=${{ env.REPO }}/${{ env.SERVICE }}:buildcache,mode=max
      - uses: docker/build-push-action@v6
        with:
          context: .
          file: Dockerfile.migrations
          push: true
          tags: ${{ env.REPO }}/${{ env.SERVICE }}-migrations:${{ github.sha }}

  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment: prod            # GitHub environment with required reviewers
    permissions: { contents: read, id-token: write }
    steps:
      - uses: google-github-actions/auth@v2
        with:
          workload_identity_provider: ${{ env.WIF_PROVIDER }}
          service_account: ${{ env.CI_SA }}
      - uses: google-github-actions/setup-gcloud@v2

      - name: Run migrations
        run: |
          gcloud run jobs update ${SERVICE}-migrate --region=$REGION \
            --image=$REPO/$SERVICE-migrations:$GITHUB_SHA
          gcloud run jobs execute ${SERVICE}-migrate --region=$REGION --wait

      - name: Deploy canary (no traffic)
        id: deploy
        uses: google-github-actions/deploy-cloudrun@v2
        with:
          service: ${{ env.SERVICE }}
          region: ${{ env.REGION }}
          image: ${{ needs.build.outputs.image }}
          revision_traffic: LATEST=0
          tag: canary
          flags: --revision-suffix=${{ github.sha }}

      - name: Smoke test canary
        run: |
          TOKEN=$(gcloud auth print-identity-token)
          URL=$(gcloud run services describe $SERVICE --region=$REGION --format='value(status.traffic[?tag==`canary`].url)' | head -n1)
          curl --fail --silent --show-error -H "Authorization: Bearer $TOKEN" "$URL/health/startup"

      - name: Promote
        run: gcloud run services update-traffic $SERVICE --region=$REGION --to-latest
```

Notes:

- The job definition (`gcloud run jobs update`) assumes the job was created by Terraform; CI only
  swaps the image. The same applies to the service: Terraform owns IAM, env, secrets and scaling,
  CI owns the image and traffic.
- `environment: prod` gives a manual approval gate and scoped secrets without a separate branch.
- Promote gradually for risky changes: replace the last step with
  `--to-tags canary=10`, wait on an alert-free window (see `gcp-observability`), then `--to-latest`.
- Builder cache in Artifact Registry (`buildcache` tag) keeps `dotnet restore` out of most runs.

`Dockerfile.migrations`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet tool install --global dotnet-ef && export PATH="$PATH:/root/.dotnet/tools" && \
    dotnet ef migrations bundle --project src/MyApp.Infrastructure --startup-project src/MyApp.Api \
      -r linux-x64 --self-contained -o /out/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy-chiseled-extra
WORKDIR /app
COPY --from=build /out/efbundle .
USER app
ENTRYPOINT ["./efbundle"]
```

The bundle builds the application's host at run time, so configuration providers (env vars,
`--set-secrets`) supply the connection string exactly as they do for the API.

## Cloud Build alternative

`cloudbuild.yaml`:

```yaml
substitutions:
  _REGION: europe-west1
  _REPO: europe-west1-docker.pkg.dev/${PROJECT_ID}/apps
  _SERVICE: api
options:
  logging: CLOUD_LOGGING_ONLY
  dynamicSubstitutions: true
serviceAccount: projects/${PROJECT_ID}/serviceAccounts/cloudbuild-deploy@${PROJECT_ID}.iam.gserviceaccount.com

steps:
  - id: build
    name: gcr.io/cloud-builders/docker
    args: ["build", "-t", "${_REPO}/${_SERVICE}:${SHORT_SHA}", "--cache-from", "${_REPO}/${_SERVICE}:latest", "."]
  - id: push
    name: gcr.io/cloud-builders/docker
    args: ["push", "${_REPO}/${_SERVICE}:${SHORT_SHA}"]
  - id: migrate
    name: gcr.io/google.com/cloudsdktool/cloud-sdk:slim
    entrypoint: bash
    args:
      - -c
      - |
        gcloud run jobs update ${_SERVICE}-migrate --region=${_REGION} --image=${_REPO}/${_SERVICE}-migrations:${SHORT_SHA}
        gcloud run jobs execute ${_SERVICE}-migrate --region=${_REGION} --wait
  - id: deploy
    name: gcr.io/google.com/cloudsdktool/cloud-sdk:slim
    args:
      - gcloud
      - run
      - deploy
      - ${_SERVICE}
      - --image=${_REPO}/${_SERVICE}:${SHORT_SHA}
      - --region=${_REGION}
      - --no-traffic
      - --tag=canary
      - --revision-suffix=${SHORT_SHA}
images:
  - ${_REPO}/${_SERVICE}:${SHORT_SHA}
```

Create the trigger with `gcloud builds triggers create github --repo-name=... --repo-owner=...
--branch-pattern='^main$' --build-config=cloudbuild.yaml
--service-account=projects/$PROJECT/serviceAccounts/cloudbuild-deploy@...`. Using a dedicated
build service account (instead of the legacy Cloud Build SA) requires `logging: CLOUD_LOGGING_ONLY`
and lets you scope roles exactly like the GitHub case. Promotion is a second, manual trigger or a
`gcloud run services update-traffic` run by a release engineer, so Cloud Build cannot promote a
canary that nobody looked at.
