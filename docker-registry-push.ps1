# Set variables
$registry = "registry.lan.woutervs.dev"
$apiImage = "sunswarm"
$version = "1.0.0"

$pw = Read-Host "Enter Docker registry password" -AsSecureString
$BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw)
$PlainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)

docker login $registry -u wouter -p $PlainPassword

# # ===== API =====
# # Tag API images
docker tag ${apiImage}:latest $registry/greyishcorp/${apiImage}:$version
docker tag ${apiImage}:latest $registry/greyishcorp/${apiImage}:latest

# # Push API images
docker push $registry/greyishcorp/${apiImage}:$version
docker push $registry/greyishcorp/${apiImage}:latest