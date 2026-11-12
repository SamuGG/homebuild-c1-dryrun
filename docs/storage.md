# Floor-plan storage (S3)

`infra/src/Infra/StorageStack.cs` creates `HomeBuild-Storage` in `eu-west-2`:

- **The bucket:** private, with Block Public Access, SSE-S3 encryption and SSL only. CORS allows POST from the web origin (`http://localhost:5266`) only.
- **Lifecycle rules:** objects under `incoming/` are deleted after one day, and incomplete multipart uploads are aborted after one day.
- **Removal (dev only):** `autoDeleteObjects` lets `cdk destroy` empty the bucket. CDK builds its provider role and Lambda as raw resources that `Tags.Of` does not reach, so the stack tags them directly.
- **The upload role:** `homebuild-api-uploads`. It allows only `s3:PutObject`, `s3:GetObject` and `s3:DeleteObject` on `incoming/*`, and `s3:PutObject` on `floor-plans/*`. It is trusted by this account.

## Upload flow

1. **Start.** An admin's browser asks the web app for a ticket. The web app relays the request to `POST /v2/units/{id}/floor-plan/uploads` with the user's token. The API, using the upload role's temporary credentials, returns a pre-signed POST for `incoming/{tenantId}/{unitId}/{guid}.pdf`. Its policy fixes the key, sets `Content-Type` to `application/pdf`, allows 1 byte to 10 MB, and expires after 5 minutes.
2. **Upload.** The browser posts the form fields and the file straight to S3 (`wwwroot/js/floor-plan-upload.js`).
3. **Complete.** The browser tells the web app, which calls `POST /v2/units/{id}/floor-plan` with the key. The API refuses a key for another tenant, unit or folder before touching S3. It then checks the stored object's size and type, and copies it to `floor-plans/…` before deleting the upload.

The web app has no AWS SDK and no AWS credentials. The bytes never pass through it or through the API.

A pre-signed PUT would be simpler, but it cannot limit size: a 50 MB PUT to a PUT URL succeeds. That is why HomeBuild uses a POST policy.

## Running locally

The API reads `Storage:BucketName` and `Storage:UploadRoleArn` from user-secrets. The role ARN contains the account ID, so keep it out of the repository. Store both from the stack outputs:

```bash
aws cloudformation describe-stacks --stack-name HomeBuild-Storage --query "Stacks[0].Outputs" --output json \
  | python3 -c "import json,sys; o={x['OutputKey']:x['OutputValue'] for x in json.load(sys.stdin)}; print(json.dumps({'Storage:BucketName':o['BucketName'],'Storage:UploadRoleArn':o['UploadRoleArn']}))" \
  | dotnet user-secrets set --project src/HomeBuild.Api
```

Run the API with AWS credentials that may assume the role (for example `AWS_PROFILE=<profile> AWS_REGION=eu-west-2`).

The S3 tests are opt-in: `HOMEBUILD_AWS_TESTS=1 AWS_PROFILE=<profile> AWS_REGION=eu-west-2 dotnet test`. Without `Storage:*` configured, the upload endpoints answer 503; CI never touches S3.

**Teardown:** `npx cdk destroy HomeBuild-Storage` (from `infra/`) empties and deletes the bucket and the role.
