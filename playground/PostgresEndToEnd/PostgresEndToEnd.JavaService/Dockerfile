# Use Microsoft's OpenJDK image with Maven for building
FROM mcr.microsoft.com/openjdk/jdk:17-ubuntu AS build

# Install Maven
RUN apt-get update && apt-get install -y maven && rm -rf /var/lib/apt/lists/*

# Set working directory
WORKDIR /app

# Copy pom.xml first for better layer caching
COPY pom.xml .

# Download dependencies (this layer will be cached if pom.xml doesn't change)
RUN mvn dependency:go-offline -B

# Copy source code
COPY src ./src

# Build the application
RUN mvn clean package -DskipTests

# Use Microsoft's Ubuntu-based JRE runtime image for the final container (more reliable than distroless)
FROM mcr.microsoft.com/openjdk/jdk:17-ubuntu

# Set working directory
WORKDIR /app

# Copy the built JAR from the build stage
COPY --from=build /app/target/javaservice-1.0.0.jar app.jar

# Expose the default port (can be overridden by environment variable)
EXPOSE 4567

# Set default environment variables
ENV PORT=4567

# Run the application (CMD allows the image's default java in PATH)
CMD ["java", "-jar", "app.jar"]