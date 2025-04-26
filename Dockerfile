# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
# Hoặc làm base cho final stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
# USER $APP_UID # Tạm thời comment dòng này nếu bạn không chắc chắn về UID/GID trong môi trường build CI
WORKDIR /app
EXPOSE 8080 # Đảm bảo port này khớp với cấu hình ứng dụng của bạn

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# --- BEGIN: Thêm cấu hình NuGet riêng trong Docker ---
# Khai báo các build arguments để nhận URL và Key từ lệnh docker build
ARG BAGET_URL
ARG BAGET_API_KEY

# Thêm nguồn NuGet riêng sử dụng các arguments đã nhận
# Chạy lệnh này TRƯỚC khi restore
# Đảm bảo các ARG đã được khai báo ở trên
RUN dotnet nuget add source "${BAGET_URL}" --name DevOpsNuGet --username user --password "${BAGET_API_KEY}" --store-password-in-clear-text
# --- END: Thêm cấu hình NuGet riêng trong Docker ---

# Copy file .csproj trước để tận dụng Docker layer caching
COPY ["PaymentApi/PaymentApi.csproj", "PaymentApi/"]
# Chạy restore - Lệnh này bây giờ sẽ sử dụng nguồn DevOpsNuGet đã thêm
RUN dotnet restore "./PaymentApi/PaymentApi.csproj"

# Copy toàn bộ source code còn lại
COPY . .
WORKDIR "/src/PaymentApi"
# Build ứng dụng, sử dụng BUILD_CONFIGURATION đã định nghĩa
RUN dotnet build "./PaymentApi.csproj" -c $BUILD_CONFIGURATION -o /app/build --no-restore # Thêm --no-restore vì đã restore ở bước trước

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# Publish ứng dụng, sử dụng BUILD_CONFIGURATION
RUN dotnet publish "./PaymentApi.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-build # Thêm --no-build vì đã build ở stage trước

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
# Copy kết quả publish từ stage 'publish'
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PaymentApi.dll"]